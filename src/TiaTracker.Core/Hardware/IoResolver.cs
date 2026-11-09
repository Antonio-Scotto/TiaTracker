using TiaTracker.Contracts;

namespace TiaTracker.Core.Hardware;

/// <summary>Un segnale della Lista IO: tag con indirizzo I/Q e il modulo che lo porta.</summary>
public sealed class IoSignal
{
    public string Plc { get; init; } = "";
    public string Table { get; init; } = "";
    public string Name { get; init; } = "";
    public string? DataType { get; init; }
    public PlcAddress Address { get; init; }
    public string? Comment { get; init; }

    /// <summary>Il modulo con quel byte nella sua area (null = indirizzo senza modulo nel progetto).</summary>
    public HwModuleRow? Module { get; init; }

    /// <summary>Chiave stabile per le note: PLC e nome del tag (la nota segue il tag se cambia indirizzo).</summary>
    public string Key => Plc + "|" + Name;
}

/// <summary>
/// Assegna ogni tag con indirizzo di ingresso o uscita al modulo che ha quel
/// byte nella sua area (stesso PLC). I tag di merker e quelli senza indirizzo
/// non sono segnali di campo e restano fuori dalla Lista IO.
/// </summary>
public static class IoResolver
{
    public static List<IoSignal> Resolve(HardwareExport hw, IReadOnlyList<HwModuleRow> modules)
    {
        List<IoSignal> signals = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (TagTableFile table in hw.TagTables)
        {
            foreach (HwTag tag in table.Tags)
            {
                if (!PlcAddress.TryParse(tag.Address, out PlcAddress a) || !a.IsIo)
                {
                    continue;
                }

                // Lo stesso tag in due tabelle (export ripetuto) conta una volta.
                if (!seen.Add(table.Plc + "|" + tag.Name + "|" + a))
                {
                    continue;
                }

                signals.Add(new IoSignal
                {
                    Plc = table.Plc ?? "",
                    Table = table.Name ?? "",
                    Name = tag.Name ?? "",
                    DataType = tag.DataType,
                    Address = a,
                    Comment = tag.Comment,
                    Module = ModuleOf(a, table.Plc, modules),
                });
            }
        }

        return signals.OrderBy(s => s.Plc, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Address.SortKey, StringComparer.Ordinal).ToList();
    }

    public static HwModuleRow? ModuleOf(PlcAddress a, string? plc, IReadOnlyList<HwModuleRow> modules)
    {
        foreach (HwModuleRow m in modules)
        {
            // Senza PLC noto (progetto con un solo PLC, periferia non collegata) il modulo vale per tutti.
            if (plc != null && m.Plc != null && !m.Plc.Equals(plc, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            List<HwRange> ranges = a.IsInput ? m.Inputs : m.Outputs;
            if (ranges.Any(r => r.Contains(a.Byte)))
            {
                return m;
            }
        }

        return null;
    }
}
