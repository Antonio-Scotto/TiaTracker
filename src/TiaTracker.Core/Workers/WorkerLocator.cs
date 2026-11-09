namespace TiaTracker.Core.Workers;

/// <summary>Esito della ricerca del worker: percorso (o null), da dove viene e tutti i posti guardati.</summary>
public sealed record WorkerLookup(int TiaMajor, string ExeName, string? Path, string? Origin, IReadOnlyList<string> Searched)
{
    public bool Found => Path != null;
}

/// <summary>
/// Dove sta l'exe del worker per una versione di TIA. Ordine: cartella delle
/// impostazioni (WorkerDir), worker\vNN accanto all'app, poi risalendo fino
/// alla radice del disco: worker\vNN, build di sviluppo
/// (src\TiaTracker.Worker.VNN\bin\Release|Debug) e pacchetto dist\TiaTracker.
/// Prima la risalita si fermava a 6 livelli e dall'output del publish
/// (bin\Release\net10.0-windows\win-x64) non arrivava alla radice del repository.
/// Il file system passa da <c>probe</c> (data di modifica o null): si prova senza file veri.
/// </summary>
public static class WorkerLocator
{
    public const string OriginSettings = "impostazione WorkerDir";
    public const string OriginApp = "cartella worker dell'app";
    public const string OriginNearby = "build o pacchetto vicino all'app";

    public static string FolderName(int tiaMajor) => tiaMajor <= 18 ? "v18" : "v21";

    public static string ExeName(int tiaMajor) => "tiatracker-worker-" + FolderName(tiaMajor) + ".exe";

    public static WorkerLookup Find(int tiaMajor, string appDir, string? workerDir) =>
        Find(tiaMajor, appDir, workerDir, p => File.Exists(p) ? File.GetLastWriteTimeUtc(p) : null);

    public static WorkerLookup Find(int tiaMajor, string appDir, string? workerDir, Func<string, DateTime?> probe)
    {
        string name = FolderName(tiaMajor);
        string exe = ExeName(tiaMajor);
        string project = "TiaTracker.Worker." + name.ToUpperInvariant();
        List<string> searched = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        DateTime? Probe(string path)
        {
            if (!seen.Add(path))
            {
                return null;
            }

            searched.Add(path);
            return probe(path);
        }

        if (!string.IsNullOrWhiteSpace(workerDir))
        {
            foreach (string p in new[] { System.IO.Path.Combine(workerDir, name, exe), System.IO.Path.Combine(workerDir, exe) })
            {
                if (Probe(p) != null)
                {
                    return new WorkerLookup(tiaMajor, exe, p, OriginSettings, searched);
                }
            }
        }

        string local = System.IO.Path.Combine(appDir, "worker", name, exe);
        if (Probe(local) != null)
        {
            return new WorkerLookup(tiaMajor, exe, local, OriginApp, searched);
        }

        // Al primo livello che ha un exe vince il piu' recente: una Debug fresca batte una Release vecchia.
        for (DirectoryInfo? d = new(appDir); d != null; d = d.Parent)
        {
            string[] level =
            {
                System.IO.Path.Combine(d.FullName, "worker", name, exe),
                System.IO.Path.Combine(d.FullName, "src", project, "bin", "Release", exe),
                System.IO.Path.Combine(d.FullName, "src", project, "bin", "Debug", exe),
                System.IO.Path.Combine(d.FullName, "dist", "TiaTracker", "worker", name, exe),
            };

            string? best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (string p in level)
            {
                DateTime? t = Probe(p);
                if (t != null && (best == null || t.Value > bestTime))
                {
                    best = p;
                    bestTime = t.Value;
                }
            }

            if (best != null)
            {
                return new WorkerLookup(tiaMajor, exe, best, OriginNearby, searched);
            }
        }

        return new WorkerLookup(tiaMajor, exe, null, null, searched);
    }
}
