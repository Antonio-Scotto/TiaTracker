using TiaTracker.Core.Domain;

namespace TiaTracker.Core.Status;

/// <summary>
/// La versione "di riferimento" della commessa, quella a cui si riferiscono di
/// default documenti, Lista IP e dashboard: la piu' recente In lavoro, altrimenti
/// quella caricata piu' di recente su un PLC, altrimenti la piu' recente non
/// scartata e non mancante.
/// </summary>
public static class ReferenceVersion
{
    public static ProjectVersion? Pick(IReadOnlyList<ProjectVersion> versions, IReadOnlyList<VersionLoad> currentLoads, long? forcedId = null)
    {
        if (forcedId != null && versions.FirstOrDefault(v => v.Id == forcedId) is { Missing: false } forced)
        {
            return forced;
        }

        IEnumerable<ProjectVersion> live = versions.Where(v => !v.Missing && v.State != VersionState.Scartata)
            .OrderByDescending(v => v.SortKey, StringComparer.Ordinal);

        ProjectVersion? working = live.FirstOrDefault(v => v.State == VersionState.InLavoro);
        if (working != null)
        {
            return working;
        }

        VersionLoad? lastLoad = currentLoads.Where(l => l.IsCurrent).OrderByDescending(l => l.LoadedUtc).FirstOrDefault();
        ProjectVersion? loaded = lastLoad == null ? null : versions.FirstOrDefault(v => v.Id == lastLoad.VersionId && !v.Missing);
        return loaded ?? live.FirstOrDefault();
    }
}
