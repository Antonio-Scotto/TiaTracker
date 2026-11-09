using System.Text.RegularExpressions;

namespace TiaTracker.Core.Output;

/// <summary>
/// Cosa tenere fra cio' che TiaTracker genera in TiaTrackerOut. Si riconoscono solo i nomi
/// che produce TiaTracker stesso (<see cref="OutputLayout"/>): tutto il resto non si tocca.
/// </summary>
public static class Retention
{
    /// <summary>Snapshot\&lt;versione&gt;\yyyyMMdd_HHmm_&lt;fonte&gt;_&lt;PLC o hardware&gt;.</summary>
    private static readonly Regex ExportFolder = new(@"^(?<ts>\d{8}_\d{4})_(?<src>[A-Za-z]+)(?:_(?<plc>.+))?$", RegexOptions.CultureInvariant);

    /// <summary>Confronti\&lt;base&gt;_&lt;target&gt;[_&lt;PLC&gt;]_yyyyMMdd_HHmm.md.</summary>
    private static readonly Regex CompareReport = new(@"^.+_\d{8}_\d{4}\.md$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Export grezzi di una versione da togliere: per ogni PLC (e per l'hardware) restano i
    /// <paramref name="keep"/> piu' recenti, piu' quelli protetti (es. lo snapshot di prova di
    /// cio' che e' sul PLC). Nomi non riconosciuti mai; <paramref name="keep"/> &lt;= 0 = tutti.
    /// </summary>
    public static List<string> ExportFoldersToDelete(IEnumerable<string> folderNames, int keep, IReadOnlySet<string>? protectedNames = null)
    {
        if (keep <= 0)
        {
            return new List<string>();
        }

        return folderNames
            .Select(n => (Name: n, Match: ExportFolder.Match(n)))
            .Where(x => x.Match.Success && !(protectedNames?.Contains(x.Name) ?? false))
            .GroupBy(x => x.Match.Groups["plc"].Value, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.OrderByDescending(x => x.Match.Groups["ts"].Value, StringComparer.Ordinal).Skip(keep))
            .Select(x => x.Name)
            .ToList();
    }

    public static bool IsCompareReport(string fileName) => CompareReport.IsMatch(fileName);
}
