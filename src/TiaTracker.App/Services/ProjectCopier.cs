using SharpCompress.Archives;
using SharpCompress.Common;
using TiaTracker.Core.Scanning;

namespace TiaTracker.App.Services;

/// <summary>
/// Mai aprire l'originale: il worker lavora su una copia in work\&lt;8hex&gt;\,
/// cancellata a fine job. Dalla copia restano fuori i file di lock e di
/// lavoro di TIA, altrimenti la copia risulterebbe "gia' aperta".
/// </summary>
public static class ProjectCopier
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase) { "TMP", "Logs" };

    /// <summary>
    /// TIA apre solo progetti in percorsi di al massimo 143 caratteri: la copia di lavoro sta
    /// in una cartella corta. Di norma la cartella dati (%LOCALAPPDATA%\TiaTracker\work); se e'
    /// troppo lunga (cartella dati spostata, nome utente lungo) %TEMP%\TiaTracker, in ultimo la
    /// radice del disco.
    /// </summary>
    public static IEnumerable<string> WorkRoots()
    {
        yield return DataPaths.Work;
        yield return Path.Combine(Path.GetTempPath(), "TiaTracker");
        yield return Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\", "TiaTrackerWork");
    }

    /// <summary>Lunghezza massima della radice: restano circa 80 caratteri per la cartella del progetto.</summary>
    private const int MaxWorkRootLength = 60;

    public static string WorkRoot => WorkRoots().FirstOrDefault(r => r.Length <= MaxWorkRootLength) ?? WorkRoots().Last();

    public static string NewWorkDir()
    {
        string dir = Path.Combine(WorkRoot, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Copia la cartella progetto e restituisce il percorso del .apNN nella copia.</summary>
    public static string CopyProject(string projectFolder, string projectFileName, string workDir)
    {
        DirectoryInfo source = new(projectFolder);
        string target = Path.Combine(workDir, source.Name);
        CopyDir(source, new DirectoryInfo(target), top: true);
        string copied = Path.Combine(target, projectFileName);
        if (!File.Exists(copied))
        {
            throw new FileNotFoundException("Nella copia manca il file progetto " + projectFileName);
        }

        return copied;
    }

    /// <summary>Copia di un progetto senza lock, TMP e Logs (usata anche da "Nuova versione da questa").</summary>
    public static void CopyProjectTree(DirectoryInfo source, DirectoryInfo target) => CopyDir(source, target, top: true);

    private static void CopyDir(DirectoryInfo source, DirectoryInfo target, bool top)
    {
        target.Create();
        foreach (FileInfo f in source.GetFiles())
        {
            if (f.Name.StartsWith("~PEData.", StringComparison.OrdinalIgnoreCase) ||
                f.Extension.Equals(".info", StringComparison.OrdinalIgnoreCase) ||
                f.Name.Equals("write.lock", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FileInfo copy = f.CopyTo(Path.Combine(target.FullName, f.Name), overwrite: true);
            copy.Attributes &= ~(FileAttributes.ReadOnly | FileAttributes.Hidden);
        }

        foreach (DirectoryInfo d in source.GetDirectories())
        {
            if (top && SkipDirs.Contains(d.Name))
            {
                continue;
            }

            CopyDir(d, new DirectoryInfo(Path.Combine(target.FullName, d.Name)), top: false);
        }
    }

    /// <summary>Estrae un .zip di backup TIA o un .rar e cerca il progetto dentro.</summary>
    public static string ExtractArchive(string archive, string workDir)
    {
        string target = Path.Combine(workDir, "x");
        Directory.CreateDirectory(target);
        ArchiveFactory.WriteToDirectory(archive, target, new ExtractionOptions
        {
            ExtractFullPath = true,
            Overwrite = true,
            PreserveFileTime = true,
        });

        // Il progetto e' il .apNN meno profondo (dentro c'e' a volte un sottoprogetto).
        FileInfo? project = new DirectoryInfo(target)
            .EnumerateFiles("*.ap*", SearchOption.AllDirectories)
            .Where(f => VersionNameParser.IsProjectExtension(f.Extension))
            .OrderBy(f => f.FullName.Count(c => c == Path.DirectorySeparatorChar))
            .FirstOrDefault();
        if (project == null)
        {
            throw new FileNotFoundException("Nessun progetto TIA (*.apNN) dentro " + Path.GetFileName(archive));
        }

        // Lock rimasti nell'archivio farebbero risultare il progetto aperto.
        foreach (FileInfo f in project.Directory!.EnumerateFiles("*", SearchOption.AllDirectories)
                     .Where(f => f.Name.StartsWith("~PEData.", StringComparison.OrdinalIgnoreCase) ||
                                 f.Extension.Equals(".info", StringComparison.OrdinalIgnoreCase)))
        {
            f.Attributes = FileAttributes.Normal;
            f.Delete();
        }

        return project.FullName;
    }

    public static void Cleanup(string workDir)
    {
        if (AppServices.Settings.KeepWorkFolders)
        {
            Log.Info("Cartella di lavoro conservata: " + workDir);
            return;
        }

        try
        {
            foreach (string f in Directory.EnumerateFiles(workDir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }

            Directory.Delete(workDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Cartella di lavoro non cancellata (" + ex.Message + "): " + workDir);
        }
    }

    /// <summary>All'avvio: resti di job interrotti piu' vecchi di un giorno.</summary>
    public static void PurgeOld()
    {
        foreach (string root in WorkRoots().Where(Directory.Exists))
        {
            foreach (DirectoryInfo d in new DirectoryInfo(root).GetDirectories())
            {
                if (DateTime.UtcNow - d.CreationTimeUtc > TimeSpan.FromDays(1))
                {
                    Cleanup(d.FullName);
                }
            }
        }
    }
}
