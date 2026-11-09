using System.Diagnostics;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Scanning;
using TiaTracker.Data;

namespace TiaTracker.App.Services;

public static class ScanService
{
    public const string TiaProcessName = "Siemens.Automation.Portal";

    public static bool IsTiaRunning()
    {
        Process[] processes = Process.GetProcessesByName(TiaProcessName);
        foreach (Process p in processes)
        {
            p.Dispose();
        }

        return processes.Length > 0;
    }

    /// <summary>Scansione completa sul thread corrente (deve essere il thread UI: scrive nel DB).</summary>
    public static ScanSummary Scan(ScanRoot root) => Apply(root, ScanFolder(root));

    /// <summary>Solo file system: si puo' chiamare da Task.Run.</summary>
    public static List<ScannedVersion> ScanFolder(ScanRoot root) => ProjectFolderScanner.Scan(root.Path, root.NameRegex);

    /// <summary>Allinea il DB a una scansione gia' fatta. Thread UI.</summary>
    public static ScanSummary Apply(ScanRoot root, IReadOnlyList<ScannedVersion> found)
    {
        ScanSummary summary = AppServices.Versions.ApplyScan(root.CommessaId, root, found);
        Log.Info($"Scansione {root.Path}: {summary.Found} versioni, {summary.New} nuove, {summary.Updated} aggiornate, {summary.Missing} sparite");
        return summary;
    }

    /// <summary>Scansione con la parte su disco in background e l'applicazione al DB sul thread chiamante.</summary>
    public static async Task<ScanSummary> ScanAsync(ScanRoot root)
    {
        List<ScannedVersion> found = await Task.Run(() => ScanFolder(root));
        return Apply(root, found);
    }

    public static string? Full(ScanRoot? root, string? rel)
    {
        if (root == null || rel == null)
        {
            return null;
        }

        return rel.Length == 0 ? root.Path : Path.Combine(root.Path, rel);
    }

    public static OpenState OpenStateOf(ProjectVersion v, ScanRoot? root, bool tiaRunning)
    {
        string? folder = Full(root, v.FolderRel);
        if (!v.HasFolder || folder == null || !Directory.Exists(folder))
        {
            return OpenState.Closed;
        }

        return OpenStateDetector.Detect(folder, v.ProjectName, tiaRunning, Environment.MachineName);
    }
}
