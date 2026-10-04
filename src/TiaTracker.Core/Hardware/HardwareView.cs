using System.Globalization;
using TiaTracker.Contracts;

namespace TiaTracker.Core.Hardware;

/// <summary>Un'area di indirizzi in byte (estremi inclusi) di un modulo.</summary>
public sealed record HwRange(char Area, int StartByte, int EndByte)
{
    public bool Contains(int b) => b >= StartByte && b <= EndByte;

    public override string ToString() => StartByte == EndByte
        ? "%" + Area + "B" + StartByte.ToString(CultureInfo.InvariantCulture)
        : "%" + Area + "B" + StartByte.ToString(CultureInfo.InvariantCulture) + "…" + EndByte.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Un modulo per Lista Hardware e Lista IO: stazione, slot, codici, aree di indirizzi, canali.</summary>
public sealed class HwModuleRow
{
    /// <summary>Chiave stabile (percorso dal dispositivo), per le note e le righe manuali.</summary>
    public string Key { get; init; } = "";
    public string DeviceName { get; init; } = "";

    /// <summary>Nome della stazione come in TIA (nome del modulo di testa, es. "20A1 - ET200 Main Cabinet").</summary>
    public string Station { get; init; } = "";
    public string GroupPath { get; init; } = "";

    /// <summary>PLC che indirizza il modulo (CPU della stazione o controller del sistema IO).</summary>
    public string? Plc { get; init; }
    public int? Slot { get; init; }
    public string Name { get; init; } = "";
    public string? TypeName { get; init; }
    public string? OrderNumber { get; init; }
    public string? Firmware { get; init; }
    public string? Comment { get; init; }
    public bool IsHead { get; init; }
    public bool IsCpu { get; init; }
    public List<HwRange> Inputs { get; init; } = new();
    public List<HwRange> Outputs { get; init; } = new();
    public string Channels { get; init; } = "";

    /// <summary>IP della stazione, solo sulla riga del modulo di testa.</summary>
    public string? Ip { get; init; }

    public string InputsText => string.Join(", ", Inputs);

    public string OutputsText => string.Join(", ", Outputs);
}

/// <summary>Un nodo di rete per la Lista IP.</summary>
public sealed class HwNodeRow
{
    public string Key { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public string Station { get; init; } = "";
    public string Interface { get; init; } = "";
    public string Node { get; init; } = "";
    public string? Subnet { get; init; }
    public string? NetType { get; init; }
    public string? Ip { get; init; }
    public string? Mask { get; init; }
    public string? Gateway { get; init; }
    public string? ProfinetName { get; init; }
    public string? IoSystem { get; init; }
    public string? DeviceNumber { get; init; }
    public string? Kind { get; init; }
    public string? OrderNumber { get; init; }
    public string? Mac { get; init; }
    public string? IpMode { get; init; }
}

/// <summary>
/// L'export hardware visto come elenchi piatti: moduli (Lista Hardware e IO) e
/// nodi di rete (Lista IP). Le lunghezze degli indirizzi di Openness sono in bit
/// (DI 16 = 16, F-DI 8 = 48 con il telegramma PROFIsafe): misurato su un impianto reale.
/// </summary>
public static class HardwareView
{
    public static List<HwModuleRow> Modules(HardwareExport hw)
    {
        Dictionary<string, string> owners = IoSystemOwners(hw);
        List<HwModuleRow> rows = new();
        foreach (HwDevice d in hw.Devices)
        {
            HwItem? head = Head(d);
            string station = head?.Name ?? d.Name;
            string? plc = d.PlcNames.FirstOrDefault() ?? OwnerOf(d, owners);
            string? ip = head == null ? null : NodesOf(head).Select(n => Attr(n.Attributes, "Address")).FirstOrDefault(a => a != null);
            foreach (HwItem item in d.Items)
            {
                List<HwAddress> addresses = Flatten(item).SelectMany(i => i.Addresses).Where(IsIo).ToList();
                string? order = Attr(item.Attributes, "OrderNumber");
                bool isCpu = item.Classification == "CPU" || item.PlcName != null;
                if (order == null && addresses.Count == 0 && !isCpu)
                {
                    continue; // rack virtuali, interfacce e simili
                }

                rows.Add(new HwModuleRow
                {
                    Key = item.Path ?? d.Name + "/" + item.Name,
                    DeviceName = d.Name,
                    Station = station,
                    GroupPath = d.GroupPath ?? "",
                    Plc = isCpu ? item.PlcName ?? plc : plc,
                    Slot = item.Position,
                    Name = item.Name ?? "",
                    TypeName = Attr(item.Attributes, "TypeName"),
                    OrderNumber = order,
                    Firmware = Attr(item.Attributes, "FirmwareVersion"),
                    Comment = Attr(item.Attributes, "Comment"),
                    IsHead = item == head,
                    IsCpu = isCpu,
                    Inputs = Ranges(addresses, "Input", 'I'),
                    Outputs = Ranges(addresses, "Output", 'Q'),
                    Channels = ChannelsText(Flatten(item).SelectMany(i => i.Channels)),
                    Ip = item == head ? ip : null,
                });
            }
        }

        return rows;
    }

    public static List<HwNodeRow> Nodes(HardwareExport hw)
    {
        List<HwNodeRow> rows = new();
        foreach (HwDevice d in hw.Devices)
        {
            HwItem? head = Head(d);
            foreach ((HwItem parent, HwItem itf) in Interfaces(d))
            {
                foreach (HwNode n in itf.Interface!.Nodes)
                {
                    bool useRouter = Attr(n.Attributes, "UseRouter") == "true";
                    HwIoConnector? conn = itf.Interface.Connectors.FirstOrDefault(c => c.IoSystem != null);
                    rows.Add(new HwNodeRow
                    {
                        Key = (itf.Path ?? d.Name + "/" + itf.Name) + "#" + n.Name,
                        DeviceName = d.Name,
                        Station = parent.Name ?? head?.Name ?? d.Name,
                        Interface = itf.Name ?? "",
                        Node = n.Name ?? "",
                        Subnet = n.Subnet,
                        NetType = n.NetType ?? n.NodeType,
                        Ip = Attr(n.Attributes, "Address"),
                        Mask = Attr(n.Attributes, "SubnetMask"),
                        Gateway = useRouter ? Attr(n.Attributes, "RouterAddress") : null,
                        ProfinetName = Attr(n.Attributes, "PnDeviceName"),
                        IoSystem = conn?.IoSystem ?? itf.Interface.ControlledIoSystems.FirstOrDefault(),
                        DeviceNumber = conn != null ? Attr(conn.Attributes, "PnDeviceNumber") : null,
                        Kind = Attr(parent.Attributes, "TypeName"),
                        OrderNumber = Attr(parent.Attributes, "OrderNumber"),
                        Mac = Attr(n.Attributes, "MacAddress"),
                        IpMode = Attr(n.Attributes, "IpProtocolSelection"),
                    });
                }
            }
        }

        return rows;
    }

    /// <summary>Sistema IO → PLC che lo controlla.</summary>
    public static Dictionary<string, string> IoSystemOwners(HardwareExport hw)
    {
        Dictionary<string, string> owners = new(StringComparer.OrdinalIgnoreCase);
        foreach (HwDevice d in hw.Devices)
        {
            string? plc = d.PlcNames.FirstOrDefault();
            if (plc == null)
            {
                continue;
            }

            foreach ((_, HwItem itf) in Interfaces(d))
            {
                foreach (string io in itf.Interface!.ControlledIoSystems.Where(s => s != null))
                {
                    owners.TryAdd(io, plc);
                }
            }
        }

        return owners;
    }

    /// <summary>Il modulo di testa: quello di primo livello che contiene un'interfaccia di rete (CPU, IM, DAP).</summary>
    public static HwItem? Head(HwDevice d) =>
        d.Items.FirstOrDefault(i => i.PlcName != null || i.Classification == "CPU")
        ?? d.Items.FirstOrDefault(i => Flatten(i).Any(x => x.Interface != null && x.Interface.Nodes.Count > 0));

    public static IEnumerable<HwItem> Flatten(HwItem item)
    {
        yield return item;
        foreach (HwItem child in item.Items)
        {
            foreach (HwItem x in Flatten(child))
            {
                yield return x;
            }
        }
    }

    /// <summary>Le interfacce di rete con il modulo di primo livello che le contiene (CPU, IM, DAP: il nome della stazione).</summary>
    private static IEnumerable<(HwItem Parent, HwItem Itf)> Interfaces(HwDevice d) =>
        d.Items.SelectMany(top => Flatten(top).Where(i => i.Interface != null).Select(i => (top, i)));

    private static IEnumerable<HwNode> NodesOf(HwItem item) =>
        Flatten(item).Where(i => i.Interface != null).SelectMany(i => i.Interface!.Nodes);

    private static string? OwnerOf(HwDevice d, Dictionary<string, string> owners)
    {
        foreach ((_, HwItem itf) in Interfaces(d))
        {
            foreach (HwIoConnector c in itf.Interface!.Connectors)
            {
                if (c.IoSystem != null && owners.TryGetValue(c.IoSystem, out string? plc))
                {
                    return plc;
                }
            }
        }

        return null;
    }

    private static bool IsIo(HwAddress a) =>
        a.StartAddress >= 0 && a.Length > 0 && (a.IoType == "Input" || a.IoType == "Output");

    private static List<HwRange> Ranges(IEnumerable<HwAddress> addresses, string ioType, char area) =>
        addresses.Where(a => a.IoType == ioType)
            .Select(a => new HwRange(area, a.StartAddress, a.StartAddress + Math.Max(1, (a.Length + 7) / 8) - 1))
            .Distinct()
            .OrderBy(r => r.StartByte)
            .ToList();

    private static string ChannelsText(IEnumerable<HwChannelGroup> channels)
    {
        IEnumerable<string> parts = channels
            .GroupBy(c => Abbrev(c.Type, c.IoType))
            .Where(g => g.Key.Length > 0)
            .Select(g => g.Sum(c => c.Count).ToString(CultureInfo.InvariantCulture) + " " + g.Key);
        return string.Join(", ", parts);
    }

    private static string Abbrev(string? type, string? io) => (type, io) switch
    {
        ("Digital", "Input") => "DI",
        ("Digital", "Output") => "DQ",
        ("Analog", "Input") => "AI",
        ("Analog", "Output") => "AQ",
        ("Technology", _) => "TEC",
        _ => "",
    };

    public static string? Attr(Dictionary<string, string>? attributes, string name) =>
        attributes != null && attributes.TryGetValue(name, out string? v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
}
