using System.Globalization;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
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

    /// <summary>Limite di TIA sul percorso del progetto.</summary>
    public const int MaxTiaProjectPath = 143;

    /// <summary>
    /// Copia la cartella progetto e restituisce il percorso del .apNN nella copia. La cartella
    /// della copia ha un nome corto fisso (TIA guarda solo il .apNN): il limite di 143
    /// caratteri si controlla prima di copiare, non dopo minuti di copia.
    /// </summary>
    public static string CopyProject(string projectFolder, string projectFileName, string workDir, CancellationToken ct = default)
    {
        DirectoryInfo source = new(projectFolder);
        string target = Path.Combine(workDir, "p");
        string copied = Path.Combine(target, projectFileName);
        if (copied.Length > MaxTiaProjectPath)
        {
            throw new IOException("Nome del progetto troppo lungo per TIA (" + copied.Length.ToString(CultureInfo.InvariantCulture) +
                                  " caratteri nella copia di lavoro, massimo " + MaxTiaProjectPath.ToString(CultureInfo.InvariantCulture) + "): " + copied);
        }

        EnsureFreeSpace(workDir, SizeOf(source, top: true, ct), "la copia di " + source.Name);
        CopyDir(source, new DirectoryInfo(target), top: true, ct);
        if (!File.Exists(copied))
        {
            throw new FileNotFoundException("Nella copia manca il file progetto " + projectFileName);
        }

        return copied;
    }

    /// <summary>Copia di un progetto senza lock, TMP e Logs (usata anche da "Nuova versione da questa").</summary>
    public static void CopyProjectTree(DirectoryInfo source, DirectoryInfo target, CancellationToken ct = default) => CopyDir(source, target, top: true, ct);

    private static bool Skip(FileInfo f) =>
        f.Name.StartsWith("~PEData.", StringComparison.OrdinalIgnoreCase) ||
        f.Extension.Equals(".info", StringComparison.OrdinalIgnoreCase) ||
        f.Name.Equals("write.lock", StringComparison.OrdinalIgnoreCase);

    /// <summary>Annullabile file per file: un progetto da rete puo' copiare per minuti.</summary>
    private static void CopyDir(DirectoryInfo source, DirectoryInfo target, bool top, CancellationToken ct)
    {
        target.Create();
        foreach (FileInfo f in source.GetFiles())
        {
            ct.ThrowIfCancellationRequested();
            if (Skip(f))
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

            CopyDir(d, new DirectoryInfo(Path.Combine(target.FullName, d.Name)), top: false, ct);
        }
    }

    private static long SizeOf(DirectoryInfo dir, bool top, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        long size = dir.GetFiles().Where(f => !Skip(f)).Sum(f => f.Length);
        foreach (DirectoryInfo d in dir.GetDirectories())
        {
            if (!(top && SkipDirs.Contains(d.Name)))
            {
                size += SizeOf(d, top: false, ct);
            }
        }

        return size;
    }

    /// <summary>
    /// Prima di copiare: il disco della copia di lavoro (di solito C:) deve avere posto per il
    /// progetto, un 10% in piu' e un margine per l'export. Meglio un errore chiaro che un disco pieno.
    /// </summary>
    private static void EnsureFreeSpace(string workDir, long needed, string what)
    {
        long free;
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(workDir));
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            free = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return;
        }

        long wanted = needed + needed / 10 + 200L * 1024 * 1024;
        if (free < wanted)
        {
            throw new IOException(string.Format(CultureInfo.InvariantCulture,
                "Spazio insufficiente per {0}: servono circa {1:0.0} GB in {2}, liberi {3:0.0} GB.",
                what, wanted / 1073741824.0, workDir, free / 1073741824.0));
        }
    }

    /// <summary>Estrae un .zip di backup TIA o un .rar e cerca il progetto dentro.</summary>
    public static string ExtractArchive(string archive, string workDir, CancellationToken ct = default)
    {
        string target = Path.Combine(workDir, "x");
        Directory.CreateDirectory(target);
        ExtractionOptions options = new()
        {
            ExtractFullPath = true,
            Overwrite = true,
            PreserveFileTime = true,
        };
        using (IArchive opened = ArchiveFactory.OpenArchive(archive))
        {
            EnsureFreeSpace(workDir, opened.TotalUncompressedSize, "l'estrazione di " + Path.GetFileName(archive));

            // File per file, con l'annullamento controllato a ogni file. Un .rar solido (e il 7z)
            // si legge solo in sequenza: estratto voce per voce ripartirebbe ogni volta dall'inizio.
            if (opened.IsSolid || opened.Type == ArchiveType.SevenZip)
            {
                using IReader reader = opened.ExtractAllEntries();
                while (reader.MoveToNextEntry())
                {
                    ct.ThrowIfCancellationRequested();
                    if (!reader.Entry.IsDirectory)
                    {
                        reader.WriteEntryToDirectory(target, options);
                    }
                }
            }
            else
            {
                foreach (IArchiveEntry entry in opened.Entries.Where(e => !e.IsDirectory))
                {
                    ct.ThrowIfCancellationRequested();
                    entry.WriteToDirectory(target, options);
                }
            }
        }

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

        Delete(workDir);
    }

    /// <summary>Come <see cref="Cleanup"/>, con la cancellazione (GB di copia) fuori dal thread UI.</summary>
    public static Task CleanupAsync(string workDir)
    {
        if (AppServices.Settings.KeepWorkFolders)
        {
            Log.Info("Cartella di lavoro conservata: " + workDir);
            return Task.CompletedTask;
        }

        return Task.Run(() => Delete(workDir));
    }

    private static void Delete(string workDir)
    {
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
