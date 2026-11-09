using TiaTracker.Contracts;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Hardware;
using TiaTracker.Core.Output;

namespace TiaTracker.Core.Network;

/// <summary>Esito di un allineamento della Lista IP con TIA.</summary>
public sealed record IpSyncSummary(int Added, int Updated, int Unchanged, int Missing, int Adopted)
{
    public bool Changed => Added + Updated + Missing + Adopted > 0;

    public override string ToString() =>
        $"{Added} nuove, {Updated} aggiornate, {Adopted} riconosciute fra le manuali, {Missing} non piu' in TIA, {Unchanged} invariate";
}

/// <summary>
/// Allinea la Lista IP della commessa ai nodi di rete letti da TIA. I campi di
/// rete (rete, IP, maschera, gateway, dispositivo, tipo, nome PROFINET) sono di
/// TIA; posizione, MAC e note restano dell'utente. Una riga sparita da TIA non
/// si cancella: si segna, e la decisione resta all'utente.
/// </summary>
public static class IpSync
{
    /// <summary>Righe per la Lista IP dai nodi Ethernet con un indirizzo (o un nome PROFINET).</summary>
    public static List<IpDevice> FromHardware(HardwareExport hw)
    {
        List<HwNodeRow> nodes = HardwareView.Nodes(hw);
        Dictionary<string, int> perStation = nodes.Where(Relevant).GroupBy(n => n.DeviceName).ToDictionary(g => g.Key, g => g.Count());
        return nodes.Where(Relevant).Select(n => new IpDevice
        {
            Network = n.Subnet,
            Ip = n.Ip,
            Subnet = n.Mask,
            Gateway = n.Gateway,
            // Piu' interfacce sulla stessa stazione (CPU X1/X2): si distinguono col nome del nodo.
            Name = perStation.TryGetValue(n.DeviceName, out int count) && count > 1 ? n.Station + " (" + n.Node + ")" : n.Station,
            Kind = n.Kind ?? n.OrderNumber,
            ProfinetName = n.ProfinetName,
            Mac = n.Mac,
            Source = IpSources.Tia,
            TiaKey = n.Key,
        }).ToList();
    }

    private static bool Relevant(HwNodeRow n) =>
        (n.NetType ?? "").Contains("Ethernet", StringComparison.OrdinalIgnoreCase) && (n.Ip != null || n.ProfinetName != null);

    public static (List<IpDevice> Rows, IpSyncSummary Summary) Merge(IReadOnlyList<IpDevice> existing, IReadOnlyList<IpDevice> fromTia,
        long? versionId, DateTime nowUtc)
    {
        List<IpDevice> rows = existing.Select(Clone).ToList();
        Dictionary<string, IpDevice> byKey = rows.Where(r => r.TiaKey != null)
            .GroupBy(r => r.TiaKey!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        HashSet<IpDevice> matched = new();
        int added = 0, updated = 0, unchanged = 0, adopted = 0;
        List<IpDevice> newRows = new();

        foreach (IpDevice t in fromTia)
        {
            IpDevice? row = t.TiaKey != null && byKey.TryGetValue(t.TiaKey, out IpDevice? k) ? k : null;
            bool adopt = false;
            if (row == null)
            {
                // Una riga scritta a mano con lo stesso IP (o lo stesso nome senza IP) diventa la riga di TIA.
                row = rows.FirstOrDefault(r => !matched.Contains(r) && r.TiaKey == null && IpTableFormat.IsValidIp(t.Ip) &&
                                               string.Equals(r.Ip?.Trim(), t.Ip, StringComparison.OrdinalIgnoreCase))
                      ?? rows.FirstOrDefault(r => !matched.Contains(r) && r.TiaKey == null && string.IsNullOrWhiteSpace(r.Ip) &&
                                                  !string.IsNullOrWhiteSpace(t.Name) &&
                                                  string.Equals(r.Name?.Trim(), t.Name, StringComparison.OrdinalIgnoreCase));
                adopt = row != null;
            }

            if (row == null)
            {
                IpDevice fresh = Clone(t);
                fresh.Source = IpSources.Tia;
                fresh.TiaVersionId = versionId;
                fresh.TiaSeenUtc = nowUtc;
                fresh.UpdatedUtc = nowUtc;
                newRows.Add(fresh);
                added++;
                continue;
            }

            matched.Add(row);
            bool changed = !Same(row.Network, t.Network) || !Same(row.Ip, t.Ip) || !Same(row.Subnet, t.Subnet) ||
                           !Same(row.Gateway, t.Gateway) || !Same(row.Name, t.Name) || !Same(row.Kind, t.Kind) ||
                           !Same(row.ProfinetName, t.ProfinetName) || row.TiaMissing;
            row.Network = t.Network;
            row.Ip = t.Ip;
            row.Subnet = t.Subnet;
            row.Gateway = t.Gateway;
            row.Name = t.Name;
            row.Kind = t.Kind;
            row.ProfinetName = t.ProfinetName;
            row.Mac ??= t.Mac;
            row.Source = IpSources.Tia;
            row.TiaKey = t.TiaKey;
            row.TiaVersionId = versionId;
            row.TiaSeenUtc = nowUtc;
            row.TiaMissing = false;
            if (adopt)
            {
                adopted++;
                row.UpdatedUtc = nowUtc;
            }
            else if (changed)
            {
                updated++;
                row.UpdatedUtc = nowUtc;
            }
            else
            {
                unchanged++;
            }
        }

        int missing = 0;
        foreach (IpDevice r in rows.Where(r => r.IsFromTia && !matched.Contains(r)))
        {
            if (!r.TiaMissing)
            {
                r.TiaMissing = true;
                r.UpdatedUtc = nowUtc;
                missing++;
            }
        }

        rows.AddRange(newRows.OrderBy(r => IpTableFormat.SortKey(r.Ip), StringComparer.Ordinal));
        return (rows, new IpSyncSummary(added, updated, unchanged, missing, adopted));
    }

    private static bool Same(string? a, string? b) =>
        string.Equals(string.IsNullOrWhiteSpace(a) ? null : a.Trim(), string.IsNullOrWhiteSpace(b) ? null : b.Trim(), StringComparison.Ordinal);

    private static IpDevice Clone(IpDevice d) => new()
    {
        Id = d.Id,
        CommessaId = d.CommessaId,
        Sort = d.Sort,
        Network = d.Network,
        Ip = d.Ip,
        Subnet = d.Subnet,
        Gateway = d.Gateway,
        Name = d.Name,
        Kind = d.Kind,
        ProfinetName = d.ProfinetName,
        Location = d.Location,
        Mac = d.Mac,
        Notes = d.Notes,
        UpdatedUtc = d.UpdatedUtc,
        Source = d.Source,
        TiaKey = d.TiaKey,
        TiaVersionId = d.TiaVersionId,
        TiaSeenUtc = d.TiaSeenUtc,
        TiaMissing = d.TiaMissing,
    };
}
