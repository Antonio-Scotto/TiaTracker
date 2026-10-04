using System.Globalization;
using System.Text.RegularExpressions;

namespace TiaTracker.Core.Scanning;

public sealed class BackupZip
{
    public string RelPath { get; init; } = "";

    /// <summary>Orario dall'mtime dello zip: il nome della cartella e' a 12 ore e non dice AM/PM.</summary>
    public DateTime WrittenUtc { get; init; }

    public long Length { get; init; }
}

public sealed class ScannedVersion
{
    public string ProjectName { get; set; } = "";
    public ParsedName Parsed { get; set; } = null!;
    public string? FolderRel { get; set; }
    public string? ProjectFileRel { get; set; }
    public string? RarRel { get; set; }
    public string? BackupRel { get; set; }
    public List<BackupZip> BackupZips { get; } = new();
    public int? TiaMajor { get; set; }
    public string? TiaVersionText { get; set; }
    public DateTime? LastSavedUtc { get; set; }

    public bool HasFolder => FolderRel != null;
    public bool HasRar => RarRel != null;
    public bool HasBackup => BackupRel != null;

    /// <summary>Ordinamento: dal nome se lo porta, altrimenti dalla data di salvataggio o dell'archivio.</summary>
    public string SortKey
    {
        get
        {
            string? fromName = Parsed.SortKey;
            if (fromName != null)
            {
                return fromName;
            }

            DateTime? when = LastSavedUtc ?? BackupZips.Select(z => (DateTime?)z.WrittenUtc).Max();
            return "T" + (when ?? DateTime.MinValue).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        }
    }
}

/// <summary>
/// Trova le versioni sotto una radice: cartelle con un *.apNN, .rar e cartelle
/// .backup associate per nome base. Sola lettura: non tocca mai i file.
/// </summary>
public static class ProjectFolderScanner
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".claude", "node_modules",
    };

    private static readonly Regex LastUsedVersion = new(@"LastUsed:\s*\r?\n\s*-\s*Version:\s*(?<v>[^\r\n]+)", RegexOptions.CultureInvariant);

    public static List<ScannedVersion> Scan(string root, string? customRegex = null, int maxDepth = 4)
    {
        Dictionary<string, ScannedVersion> byName = new(StringComparer.OrdinalIgnoreCase);
        DirectoryInfo rootDir = new(root);
        if (!rootDir.Exists)
        {
            throw new DirectoryNotFoundException("Cartella non trovata: " + root);
        }

        Walk(rootDir, rootDir.FullName, 0, maxDepth, customRegex, byName);

        // Un .rar o un .backup senza cartella entra solo se il nome sembra una versione:
        // un archivio di documenti non deve diventare una versione.
        return byName.Values
            .Where(v => v.HasFolder || v.HasBackup || v.Parsed.Kind != NameKind.Fallback)
            .OrderByDescending(v => v.SortKey, StringComparer.Ordinal)
            .ToList();
    }

    private static void Walk(DirectoryInfo dir, string root, int depth, int maxDepth, string? regex, Dictionary<string, ScannedVersion> byName)
    {
        FileInfo[] files;
        DirectoryInfo[] subdirs;
        try
        {
            files = dir.GetFiles();
            subdirs = dir.GetDirectories();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        List<FileInfo> projectFiles = files.Where(f => VersionNameParser.IsProjectExtension(f.Extension)).ToList();
        // Una cartella progetto non si esplora: dentro HIPERCAST c'e' un secondo
        // .ap15_1 (ROBOT Roller) che non e' una versione.
        if (projectFiles.Count > 0)
        {
            RegisterProjectFolder(dir, projectFiles, root, regex, byName);
            return;
        }

        foreach (FileInfo rar in files.Where(f => f.Extension.Equals(".rar", StringComparison.OrdinalIgnoreCase)))
        {
            string name = Path.GetFileNameWithoutExtension(rar.Name);
            ScannedVersion v = Get(byName, name, regex);
            v.RarRel ??= Path.GetRelativePath(root, rar.FullName);
            v.TiaMajor ??= v.Parsed.TiaFromName;
        }

        foreach (DirectoryInfo sub in subdirs)
        {
            if (sub.Name.EndsWith(".backup", StringComparison.OrdinalIgnoreCase))
            {
                RegisterBackup(sub, root, regex, byName);
                continue;
            }

            if (depth >= maxDepth || SkipDirs.Contains(sub.Name) || (sub.Attributes & FileAttributes.Hidden) != 0)
            {
                continue;
            }

            Walk(sub, root, depth + 1, maxDepth, regex, byName);
        }
    }

    private static void RegisterProjectFolder(DirectoryInfo dir, List<FileInfo> projectFiles, string root, string? regex, Dictionary<string, ScannedVersion> byName)
    {
        // Se ci sono piu' .apNN si preferisce quello col nome della cartella.
        FileInfo project = projectFiles.FirstOrDefault(f =>
                               Path.GetFileNameWithoutExtension(f.Name).Equals(dir.Name, StringComparison.OrdinalIgnoreCase))
                           ?? projectFiles.OrderByDescending(f => f.LastWriteTimeUtc).First();

        string name = Path.GetFileNameWithoutExtension(project.Name);
        ScannedVersion v = Get(byName, name, regex);
        v.FolderRel = Rel(root, dir.FullName);
        v.ProjectFileRel = Path.GetRelativePath(root, project.FullName);

        (int Major, string Text)? tia = VersionNameParser.TiaFromExtension(project.Extension);
        if (tia.HasValue)
        {
            v.TiaMajor = tia.Value.Major;
            v.TiaVersionText = tia.Value.Text;
        }

        string info = Path.Combine(dir.FullName, "ProjectInfo.txt");
        if (File.Exists(info))
        {
            try
            {
                Match m = LastUsedVersion.Match(File.ReadAllText(info));
                if (m.Success)
                {
                    v.TiaVersionText = m.Groups["v"].Value.Trim();
                }
            }
            catch (IOException)
            {
            }
        }

        // Il .apNN e' un guscio di dimensione fissa: l'ultimo salvataggio vero e' PEData.plf.
        FileInfo plf = new(Path.Combine(dir.FullName, "System", "PEData.plf"));
        v.LastSavedUtc = plf.Exists ? plf.LastWriteTimeUtc : project.LastWriteTimeUtc;
    }

    private static void RegisterBackup(DirectoryInfo dir, string root, string? regex, Dictionary<string, ScannedVersion> byName)
    {
        string name = dir.Name[..^".backup".Length];
        ScannedVersion v = Get(byName, name, regex);
        v.BackupRel = Rel(root, dir.FullName);
        v.TiaMajor ??= v.Parsed.TiaFromName;
        try
        {
            foreach (FileInfo zip in dir.EnumerateFiles("*.zip", SearchOption.AllDirectories))
            {
                v.BackupZips.Add(new BackupZip
                {
                    RelPath = Path.GetRelativePath(root, zip.FullName),
                    WrittenUtc = zip.LastWriteTimeUtc,
                    Length = zip.Length,
                });
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
        }

        v.BackupZips.Sort((a, b) => b.WrittenUtc.CompareTo(a.WrittenUtc));
    }

    private static ScannedVersion Get(Dictionary<string, ScannedVersion> byName, string name, string? regex)
    {
        if (!byName.TryGetValue(name, out ScannedVersion? v))
        {
            v = new ScannedVersion { ProjectName = name, Parsed = VersionNameParser.Parse(name, regex) };
            byName[name] = v;
        }

        return v;
    }

    private static string Rel(string root, string full)
    {
        string rel = Path.GetRelativePath(root, full);
        return rel == "." ? "" : rel;
    }
}
