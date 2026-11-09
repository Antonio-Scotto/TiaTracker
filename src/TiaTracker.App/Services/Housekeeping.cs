using System.Globalization;
using System.Text.RegularExpressions;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Output;

namespace TiaTracker.App.Services;

/// <summary>
/// Pulizia automatica di cio' che TiaTracker stesso genera e non rilegge, perche' il disco
/// non si riempia: log vecchi, resti di lavori interrotti, export grezzi in
/// TiaTrackerOut\Snapshot oltre gli ultimi N per versione, report dei confronti vecchi,
/// export hardware superati e contenuti senza piu' riferimenti nell'archivio.
/// Mai cartelle delle versioni, .rar, backup di TIA, Elenchi o Documenti: si toccano solo
/// nomi che TiaTracker riconosce come suoi (Retention), e i progetti si cancellano solo
/// dall'utente ("Elimina...").
/// </summary>
public static class Housekeeping
{
    public const int LogDays = 30;
    public const int CompareReportDays = 90;
    public const int HardwarePerVersion = 3;

    /// <summary>La ricerca dei contenuti orfani scorre tutto l'archivio: solo dopo una potatura o una volta a settimana.</summary>
    private static readonly TimeSpan ArchiveCleanupEvery = TimeSpan.FromDays(7);

    private static readonly TimeSpan Leftovers = TimeSpan.FromDays(1);
    private static readonly Regex LogFile = new(@"^tiatracker_\d{8}\.log$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RequestFile = new(@"^request_[0-9a-f]{8}\.json$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ExportTemp = new(@"\.tiatracker-[0-9a-f]{8}\.tmp$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Le cartelle di una commessa da ripulire (calcolate sul thread UI, usate in background).</summary>
    private sealed record Target(string Snapshots, string Compares, string Lists, IReadOnlySet<string> Protected);

    private sealed class Tally
    {
        public int Items;
        public long Bytes;
        public readonly List<string> Notes = new();

        public void Add(int items, long bytes, string what)
        {
            if (items == 0)
            {
                return;
            }

            Items += items;
            Bytes += bytes;
            Notes.Add(items + " " + what + (bytes > 0 ? " (" + Size(bytes) + ")" : ""));
        }
    }

    /// <summary>
    /// Dopo l'avvio: archivio sul thread UI (se non c'e' un lavoro in corso), file in background.
    /// Il riepilogo, o null se non c'era niente da togliere.
    /// </summary>
    public static async Task<string?> RunAsync()
    {
        Tally t = new();
        if (!JobRunner.IsRunning)
        {
            try
            {
                int pruned = AppServices.Hardware.Prune(HardwarePerVersion);
                t.Add(pruned, 0, "export hardware superati");
                DateTime? last = AppServices.Settings.LastArchiveCleanupUtc;
                if (pruned > 0 || last == null || DateTime.UtcNow - last.Value > ArchiveCleanupEvery)
                {
                    // Righe tolte dal DB: il file non cala (SQLite riusa lo spazio), quindi niente "liberati".
                    (int count, long bytes) = AppServices.Content.DeleteOrphans();
                    t.Add(count, 0, "contenuti senza riferimenti nell'archivio" + (count > 0 ? " (" + Size(bytes) + " riusabili dal DB)" : ""));
                    AppServices.Settings.LastArchiveCleanupUtc = DateTime.UtcNow;
                    AppServices.SaveSettings();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Pulizia dell'archivio", ex);
            }
        }

        List<Target> targets = AllTargets();
        int keep = AppServices.Settings.KeepSnapshotExports;
        bool keepWork = AppServices.Settings.KeepWorkFolders;

        await Task.Run(() =>
        {
            CleanAppFolders(t, keepWork);
            foreach (Target target in targets)
            {
                CleanOutput(target, keep, t);
            }
        });

        if (t.Items == 0)
        {
            Log.Info("Pulizia automatica: niente da togliere");
            return null;
        }

        string summary = string.Join(", ", t.Notes) + (t.Bytes > 0 ? "; liberati " + Size(t.Bytes) + " su disco" : "");
        Log.Info("Pulizia automatica: " + summary);
        return summary;
    }

    /// <summary>Dopo uno snapshot: gli export grezzi della versione oltre gli ultimi N.</summary>
    public static async Task AfterSnapshotAsync(ProjectVersion version)
    {
        // La TiaTrackerOut puo' essere di piu' commesse: valgono le protezioni di tutte.
        string? snapshots = Of(version.CommessaId)?.Snapshots;
        if (AllTargets().FirstOrDefault(x => string.Equals(x.Snapshots, snapshots, StringComparison.OrdinalIgnoreCase)) is not Target target)
        {
            return;
        }

        int keep = AppServices.Settings.KeepSnapshotExports;
        string dir = Path.Combine(target.Snapshots, OutputLayout.Safe(version.Label));
        Tally t = new();
        await Task.Run(() => PruneExports(dir, keep, target.Protected, t));
        if (t.Items > 0)
        {
            Log.Info("Pulizia dopo lo snapshot di " + version.Label + ": " + string.Join(", ", t.Notes));
        }
    }

    /// <summary>
    /// Una cartella per TiaTrackerOut. Piu' commesse possono condividerla (di default sta nella
    /// prima cartella di scansione): gli export protetti sono quelli di tutte, non della prima
    /// soltanto, altrimenti la pulizia toglieva lo snapshot di prova del PLC di un'altra commessa.
    /// </summary>
    private static List<Target> AllTargets() =>
        AppServices.Commesse.All()
            .Select(c => Of(c.Id))
            .OfType<Target>()
            .GroupBy(x => x.Snapshots, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First() with
            {
                Protected = g.SelectMany(x => x.Protected).ToHashSet(StringComparer.OrdinalIgnoreCase),
            })
            .ToList();

    private static Target? Of(long commessaId)
    {
        OutputLayout? layout = OutputService.Layout(commessaId);
        if (layout == null)
        {
            return null;
        }

        // Lo snapshot di prova di cio' che e' sul PLC resta anche se e' vecchio.
        Dictionary<long, string> labels = AppServices.Versions.ByCommessa(commessaId).ToDictionary(v => v.Id, v => v.Label);
        HashSet<string> keep = new(StringComparer.OrdinalIgnoreCase);
        foreach (VersionLoad l in AppServices.Versions.CurrentLoads(commessaId).Where(l => l.ProofSnapshotId != null))
        {
            if (AppServices.Snapshots.Get(l.ProofSnapshotId!.Value) is Snapshot s && labels.TryGetValue(s.VersionId, out string? label))
            {
                keep.Add(Path.GetFileName(layout.SnapshotDir(label, s.CreatedUtc, s.Source, s.PlcName)));
            }
        }

        return new Target(layout.Snapshots, layout.Compares, layout.Lists, keep);
    }

    // ---------- in background: solo file ----------

    private static void CleanAppFolders(Tally t, bool keepWork)
    {
        int n = 0;
        long bytes = 0;
        foreach (FileInfo f in Files(DataPaths.Logs, "*.log").Where(f => LogFile.IsMatch(f.Name) && Age(f) > TimeSpan.FromDays(LogDays)))
        {
            Delete(f, ref n, ref bytes);
        }

        t.Add(n, bytes, "log piu' vecchi di " + LogDays + " giorni");

        if (keepWork)
        {
            return;
        }

        n = 0;
        bytes = 0;
        foreach (FileInfo f in Files(DataPaths.Work, "request_*.json").Where(f => RequestFile.IsMatch(f.Name) && Age(f) > Leftovers))
        {
            Delete(f, ref n, ref bytes);
        }

        t.Add(n, bytes, "richieste ai worker rimaste da lavori interrotti");
    }

    private static void CleanOutput(Target target, int keep, Tally t)
    {
        if (Directory.Exists(target.Snapshots))
        {
            foreach (DirectoryInfo version in SafeDirs(target.Snapshots))
            {
                PruneExports(version.FullName, keep, target.Protected, t);
            }
        }

        int n = 0;
        long bytes = 0;
        foreach (FileInfo f in Files(target.Compares, "*.md").Where(f => Retention.IsCompareReport(f.Name) && Age(f) > TimeSpan.FromDays(CompareReportDays)))
        {
            Delete(f, ref n, ref bytes);
        }

        t.Add(n, bytes, "report di confronto piu' vecchi di " + CompareReportDays + " giorni");

        // File temporanei dei documenti rimasti da una generazione interrotta (mai i documenti).
        n = 0;
        bytes = 0;
        foreach (FileInfo f in Files(target.Lists, "*.tmp", SearchOption.AllDirectories).Where(f => ExportTemp.IsMatch(f.Name) && Age(f) > Leftovers))
        {
            Delete(f, ref n, ref bytes);
        }

        t.Add(n, bytes, "file temporanei dei documenti");
    }

    private static void PruneExports(string versionDir, int keep, IReadOnlySet<string> protectedNames, Tally t)
    {
        if (!Directory.Exists(versionDir))
        {
            return;
        }

        List<DirectoryInfo> dirs = SafeDirs(versionDir).ToList();
        HashSet<string> drop = new(Retention.ExportFoldersToDelete(dirs.Select(d => d.Name), keep, protectedNames), StringComparer.OrdinalIgnoreCase);
        int n = 0;
        long bytes = 0;
        foreach (DirectoryInfo d in dirs.Where(d => drop.Contains(d.Name)))
        {
            long size = SizeOf(d);
            try
            {
                d.Delete(recursive: true);
                n++;
                bytes += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Export grezzo non tolto (" + ex.Message + "): " + d.FullName);
            }
        }

        t.Add(n, bytes, "export grezzi di " + Path.GetFileName(versionDir) + " oltre gli ultimi " + keep);
    }

    /// <summary>Sottocartelle vere (niente collegamenti o junction, che porterebbero altrove).</summary>
    private static IEnumerable<DirectoryInfo> SafeDirs(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).GetDirectories().Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<DirectoryInfo>();
        }
    }

    private static IEnumerable<FileInfo> Files(string dir, string pattern, SearchOption option = SearchOption.TopDirectoryOnly)
    {
        try
        {
            return Directory.Exists(dir) ? new DirectoryInfo(dir).GetFiles(pattern, option).ToList() : Array.Empty<FileInfo>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<FileInfo>();
        }
    }

    private static void Delete(FileInfo f, ref int n, ref long bytes)
    {
        try
        {
            long size = f.Length;
            f.Delete();
            n++;
            bytes += size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("File non tolto (" + ex.Message + "): " + f.FullName);
        }
    }

    private static long SizeOf(DirectoryInfo d)
    {
        try
        {
            return d.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static TimeSpan Age(FileInfo f) => DateTime.UtcNow - f.LastWriteTimeUtc;

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (bytes / 1024) + " KB",
        _ => bytes + " B",
    };
}
