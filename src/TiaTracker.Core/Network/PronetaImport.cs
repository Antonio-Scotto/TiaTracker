using TiaTracker.Core.Domain;

namespace TiaTracker.Core.Network;

public enum PronetaMatchKind
{
    /// <summary>Stessa riga della Lista IP (nome PROFINET, IP o MAC).</summary>
    Matched,

    /// <summary>Trovato in rete ma non nella Lista IP.</summary>
    New,
}

/// <summary>Un dispositivo di PRONETA con la riga della Lista IP che gli corrisponde.</summary>
public sealed class PronetaMatch
{
    public PronetaDevice Device { get; init; } = new();

    /// <summary>Indice della riga nella Lista IP (null = nuovo).</summary>
    public int? RowIndex { get; init; }
    public PronetaMatchKind Kind { get; init; }

    /// <summary>"nome PROFINET", "IP" o "MAC".</summary>
    public string? MatchedBy { get; init; }

    /// <summary>IP in tabella diverso da quello trovato in rete (la tabella non si tocca: si segnala).</summary>
    public string? TableIp { get; init; }
    public bool IpDiffers { get; init; }

    /// <summary>Il MAC si aggiunge o cambia.</summary>
    public bool MacChanges { get; init; }

    /// <summary>Proposta: applicare la riga (i nuovi PC no).</summary>
    public bool Apply { get; set; }

    public string Outcome => Kind == PronetaMatchKind.New ? "Nuovo (solo in rete)"
        : IpDiffers ? "IP diverso (per " + MatchedBy + ")"
        : "Per " + MatchedBy + (MacChanges ? " · nuovo MAC" : "");
}

/// <summary>
/// Dispositivi trovati in rete da PRONETA nella Lista IP: si abbinano per nome
/// PROFINET, poi per IP, poi per MAC. Alle righe abbinate si aggiunge il MAC (TIA
/// non lo conosce) e si riempiono i campi vuoti; un IP diverso non si corregge da
/// solo ma si segnala. I dispositivi solo in rete si possono aggiungere.
/// </summary>
public static class PronetaImport
{
    public static List<PronetaMatch> Plan(IReadOnlyList<IpDevice> rows, IReadOnlyList<PronetaDevice> devices)
    {
        HashSet<int> used = new();
        List<PronetaMatch> plan = new();
        foreach (PronetaDevice d in devices)
        {
            (int index, string by) = Find(rows, d, used);
            if (index < 0)
            {
                plan.Add(new PronetaMatch { Device = d, Kind = PronetaMatchKind.New, Apply = !d.IsPc && !string.IsNullOrWhiteSpace(d.Ip) });
                continue;
            }

            used.Add(index);
            IpDevice row = rows[index];
            bool ipDiffers = !string.IsNullOrWhiteSpace(row.Ip) && !string.IsNullOrWhiteSpace(d.Ip) &&
                             !string.Equals(row.Ip.Trim(), d.Ip, StringComparison.OrdinalIgnoreCase);
            bool macChanges = PronetaCsv.MacKey(d.Mac) != null && PronetaCsv.MacKey(row.Mac) != PronetaCsv.MacKey(d.Mac);
            plan.Add(new PronetaMatch
            {
                Device = d,
                RowIndex = index,
                Kind = PronetaMatchKind.Matched,
                MatchedBy = by,
                TableIp = row.Ip,
                IpDiffers = ipDiffers,
                MacChanges = macChanges,
                Apply = true,
            });
        }

        return plan;
    }

    /// <summary>Applica le righe scelte: restituisce la nuova Lista IP (le righe esistenti restano al loro posto).</summary>
    public static List<IpDevice> Apply(IReadOnlyList<IpDevice> rows, IEnumerable<PronetaMatch> plan)
    {
        List<IpDevice> result = rows.ToList();
        foreach (PronetaMatch m in plan.Where(p => p.Apply))
        {
            PronetaDevice d = m.Device;
            if (m.RowIndex is int i)
            {
                IpDevice r = result[i];
                r.Mac = PronetaCsv.FormatMac(d.Mac) ?? r.Mac;
                r.Ip = string.IsNullOrWhiteSpace(r.Ip) ? d.Ip : r.Ip;
                r.Subnet = string.IsNullOrWhiteSpace(r.Subnet) ? d.Mask : r.Subnet;
                r.ProfinetName = string.IsNullOrWhiteSpace(r.ProfinetName) ? d.Name : r.ProfinetName;
                r.Kind = string.IsNullOrWhiteSpace(r.Kind) ? d.DeviceType : r.Kind;
                r.Gateway = string.IsNullOrWhiteSpace(r.Gateway) ? d.Gateway : r.Gateway;
                continue;
            }

            result.Add(new IpDevice
            {
                Name = DisplayName(d.Name),
                Ip = d.Ip,
                Subnet = d.Mask,
                Gateway = d.Gateway,
                Mac = PronetaCsv.FormatMac(d.Mac),
                ProfinetName = d.Name,
                Kind = d.DeviceType,
                Source = IpSources.Proneta,
            });
        }

        return result;
    }

    /// <summary>"plc_1.profinet interface_1" → "plc_1": il suffisso dell'interfaccia non serve come nome.</summary>
    public static string DisplayName(string pnName)
    {
        int dot = pnName.IndexOf(".profinet interface", StringComparison.OrdinalIgnoreCase);
        return dot > 0 ? pnName[..dot] : pnName;
    }

    private static (int Index, string By) Find(IReadOnlyList<IpDevice> rows, PronetaDevice d, HashSet<int> used)
    {
        int Search(Func<IpDevice, bool> match)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (!used.Contains(i) && match(rows[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        int byName = Search(r => !string.IsNullOrWhiteSpace(r.ProfinetName) &&
                                 string.Equals(r.ProfinetName.Trim(), d.Name, StringComparison.OrdinalIgnoreCase));
        if (byName >= 0)
        {
            return (byName, "nome PROFINET");
        }

        int byIp = string.IsNullOrWhiteSpace(d.Ip) ? -1
            : Search(r => string.Equals(r.Ip?.Trim(), d.Ip, StringComparison.OrdinalIgnoreCase));
        if (byIp >= 0)
        {
            return (byIp, "IP");
        }

        string? mac = PronetaCsv.MacKey(d.Mac);
        int byMac = mac == null ? -1 : Search(r => PronetaCsv.MacKey(r.Mac) == mac);
        return byMac >= 0 ? (byMac, "MAC") : (-1, "");
    }
}
