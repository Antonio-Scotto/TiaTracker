using System.Text;

namespace TiaTracker.Core.Network;

/// <summary>Un dispositivo (o un'interfaccia in piu') trovato in rete da PRONETA.</summary>
public sealed class PronetaDevice
{
    public int Number { get; init; }

    /// <summary>Nome PROFINET come lo vede la rete (es. "20a1 - et200 main cabinet").</summary>
    public string Name { get; init; } = "";
    public string? DeviceType { get; init; }
    public string? Ip { get; init; }
    public string? Mask { get; init; }
    public string? Mac { get; init; }
    public string? Role { get; init; }
    public string? IoController { get; init; }
    public string? Vendor { get; init; }
    public string? OrderNumber { get; init; }
    public string? Firmware { get; init; }
    public string? SerialNumber { get; init; }

    /// <summary>Router configurato nel dispositivo (null se non c'e': PRONETA ripete l'IP del dispositivo).</summary>
    public string? Gateway { get; init; }

    /// <summary>Seconda interfaccia dello stesso dispositivo (es. X2 di una CPU), riga di continuazione.</summary>
    public bool IsExtraInterface { get; init; }

    /// <summary>Un PC (portatile di messa in servizio, SIMATIC-PC): di solito non va nella Lista IP.</summary>
    public bool IsPc => (DeviceType ?? "").Contains("PC", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(IoController);
}

/// <summary>
/// L'export CSV della topologia online di PRONETA (Siemens): riga "sep=;",
/// titolo della sezione, intestazione con gruppi di colonne ripetuti
/// (dispositivo, interfaccia, porta, modulo) e righe di continuazione per
/// porte, moduli e interfacce in piu'. Si leggono dispositivi e interfacce.
/// </summary>
public static class PronetaCsv
{
    private static readonly string[] NumberNames = { "#", "n.", "nr." };
    private static readonly string[] NameNames = { "Name", "Nome", "Gerätename", "Device Name", "Nome dispositivo" };
    private static readonly string[] TypeNames = { "Device Type", "Tipo di dispositivo", "Tipo dispositivo", "Gerätetyp" };
    private static readonly string[] IpNames = { "IP Address", "Indirizzo IP", "IP-Adresse" };
    private static readonly string[] MaskNames = { "Subnet Mask", "Maschera di sottorete", "Subnetzmaske", "Subnet mask" };
    private static readonly string[] MacNames = { "MAC Address", "Indirizzo MAC", "MAC-Adresse" };
    private static readonly string[] RoleNames = { "Role", "Ruolo", "Rolle" };
    private static readonly string[] ControllerNames = { "IO Controller", "IO-Controller", "Controller IO" };
    private static readonly string[] VendorNames = { "Vendor Name", "Vendor", "Costruttore", "Produttore", "Hersteller" };
    private static readonly string[] OrderNames = { "Order Number", "Numero di articolo", "Codice articolo", "Bestellnummer", "Artikelnummer" };
    private static readonly string[] FirmwareNames = { "Firmware Version", "Versione firmware", "Firmware-Version" };

    /// <summary>Il testo sembra un export di PRONETA (intestazione con IP e MAC e il gruppo ripetuto delle interfacce).</summary>
    public static bool LooksLikeProneta(string text)
    {
        foreach (List<string> row in Rows(text).Take(12))
        {
            if (Index(row, IpNames, 0) >= 0 && Index(row, MacNames, 0) >= 0 && Index(row, TypeNames, 0) >= 0)
            {
                return true;
            }
        }

        return text.Contains("Online Topology", StringComparison.OrdinalIgnoreCase);
    }

    public static List<PronetaDevice> Parse(string text)
    {
        List<List<string>> rows = Rows(text).ToList();
        int header = rows.FindIndex(r => Index(r, IpNames, 0) >= 0 && Index(r, MacNames, 0) >= 0);
        if (header < 0)
        {
            return new List<PronetaDevice>();
        }

        List<string> h = rows[header];
        int number = Index(h, NumberNames, 0);
        int name = Index(h, NameNames, 0);
        int type = Index(h, TypeNames, 0);
        int ip = Index(h, IpNames, 0);
        int mask = Index(h, MaskNames, 0);
        int mac = Index(h, MacNames, 0);
        int role = Index(h, RoleNames, 0);
        int controller = Index(h, ControllerNames, 0);
        int vendor = Index(h, VendorNames, 0);
        int order = Index(h, OrderNames, 0);
        int firmware = Index(h, FirmwareNames, 0);

        // Secondo gruppo: l'interfaccia (#, Name, IP Address, Subnet Mask, MAC Address).
        int itfNumber = Index(h, NumberNames, Math.Max(mac, ip) + 1);
        int itfName = Index(h, NameNames, itfNumber + 1);
        int itfIp = Index(h, IpNames, itfNumber + 1);
        int itfMask = Index(h, MaskNames, itfNumber + 1);
        int itfMac = Index(h, MacNames, itfNumber + 1);

        List<PronetaDevice> devices = new();
        PronetaDevice? current = null;
        for (int i = header + 1; i < rows.Count; i++)
        {
            List<string> r = rows[i];
            if (int.TryParse(Cell(r, number), out int n))
            {
                current = new PronetaDevice
                {
                    Number = n,
                    Name = Cell(r, name) ?? "",
                    DeviceType = Cell(r, type),
                    Ip = Cell(r, ip),
                    Mask = Cell(r, mask),
                    Mac = Cell(r, mac),
                    Role = Cell(r, role),
                    IoController = Cell(r, controller),
                    Vendor = Cell(r, vendor),
                    OrderNumber = Cell(r, order),
                    Firmware = Cell(r, firmware),
                };
                devices.Add(current);
                continue;
            }

            // Riga di continuazione con un'altra interfaccia (#2, #3...) del dispositivo corrente.
            if (current != null && itfNumber >= 0 && int.TryParse(Cell(r, itfNumber), out int k) && k > 1 && Cell(r, itfIp) != null)
            {
                devices.Add(new PronetaDevice
                {
                    Number = current.Number,
                    Name = Cell(r, itfName) ?? current.Name,
                    DeviceType = current.DeviceType,
                    Ip = Cell(r, itfIp),
                    Mask = Cell(r, itfMask),
                    Mac = Cell(r, itfMac),
                    Role = current.Role,
                    IoController = current.IoController,
                    Vendor = current.Vendor,
                    OrderNumber = current.OrderNumber,
                    Firmware = current.Firmware,
                    IsExtraInterface = true,
                });
            }
        }

        return devices;
    }

    /// <summary>MAC confrontabile: solo cifre esadecimali maiuscole.</summary>
    public static string? MacKey(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac))
        {
            return null;
        }

        string hex = new(mac.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
        return hex.Length == 12 ? hex : null;
    }

    /// <summary>MAC nel formato di TIA: 02-00-00-00-01-00.</summary>
    public static string? FormatMac(string? mac)
    {
        string? key = MacKey(mac);
        return key == null ? mac?.Trim() : string.Join("-", Enumerable.Range(0, 6).Select(i => key.Substring(i * 2, 2)));
    }

    private static string? Cell(List<string> row, int index)
    {
        if (index < 0 || index >= row.Count)
        {
            return null;
        }

        string v = row[index].Trim();
        return v.Length == 0 ? null : v;
    }

    private static int Index(List<string> row, string[] names, int from)
    {
        for (int i = Math.Max(0, from); i < row.Count; i++)
        {
            if (names.Any(n => string.Equals(row[i].Trim(), n, StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Righe CSV con separatore ';' (o ',' se manca), virgolette doppie e "" come escape.</summary>
    private static IEnumerable<List<string>> Rows(string text)
    {
        string body = text.TrimStart('﻿');
        char sep = ';';
        if (body.StartsWith("sep=", StringComparison.OrdinalIgnoreCase))
        {
            int nl = body.IndexOf('\n');
            sep = body.Length > 4 ? body[4] : ';';
            body = nl < 0 ? "" : body[(nl + 1)..];
        }
        else if (!body.Contains(';') && body.Contains(','))
        {
            sep = ',';
        }

        List<string> row = new();
        StringBuilder cell = new();
        bool quoted = false;
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < body.Length && body[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }

                continue;
            }

            if (c == '"')
            {
                quoted = true;
            }
            else if (c == sep)
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (c == '\n')
            {
                row.Add(cell.ToString().TrimEnd('\r'));
                cell.Clear();
                yield return row;
                row = new List<string>();
            }
            else
            {
                cell.Append(c);
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString().TrimEnd('\r'));
            yield return row;
        }
    }
}
