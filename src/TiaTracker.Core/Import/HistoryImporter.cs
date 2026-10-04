using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TiaTracker.Core.Import;

public sealed class HistoryProposal
{
    public DraftChange Draft { get; init; } = null!;

    /// <summary>Percorsi relativi alla radice e SHA-256 dei file da cui viene.</summary>
    public List<(string RelPath, string Sha256)> Sources { get; } = new();

    public bool AlreadyImported { get; set; }
}

/// <summary>
/// Trova i documenti storici sotto una radice, li legge e propone bozze.
/// Un LEGGIMI/README che cita un doc\*.md si fonde con quel documento.
/// </summary>
public static class HistoryImporter
{
    public static readonly string[] DefaultGlobs =
    {
        @"doc\Modifiche_*.md",
        @"doc\Fattibilita_*.md",
        @"_tia\*\README*.md",
        @"_tia\*\LEGGIMI*.md",
        @"_tia\*\PIANO*.md",
        @"_tia\*\*\README*.md",
    };

    public static IReadOnlyList<string> Globs(string? configured) =>
        string.IsNullOrWhiteSpace(configured)
            ? DefaultGlobs
            : configured.Split(new[] { '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(g => g.Trim()).Where(g => g.Length > 0).ToArray();

    public static List<string> FindFiles(string root, IReadOnlyList<string> globs)
    {
        HashSet<string> found = new(StringComparer.OrdinalIgnoreCase);
        foreach (string glob in globs)
        {
            foreach (string f in Expand(root, glob.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries), 0))
            {
                found.Add(Path.GetRelativePath(root, f));
            }
        }

        return found.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> Expand(string dir, string[] parts, int index)
    {
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        string part = parts[index];
        bool last = index == parts.Length - 1;
        IEnumerable<string> matches;
        try
        {
            matches = last ? Directory.EnumerateFiles(dir, part) : Directory.EnumerateDirectories(dir, part);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (string m in matches)
        {
            if (last)
            {
                yield return m;
            }
            else
            {
                foreach (string f in Expand(m, parts, index + 1))
                {
                    yield return f;
                }
            }
        }
    }

    public static string Sha256File(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    public static List<HistoryProposal> Propose(string root, IReadOnlyList<string> relFiles, IReadOnlyCollection<string> knownLabels, Func<string, bool> isImported)
    {
        Dictionary<string, (DraftChange Draft, string Sha)> parsed = new(StringComparer.OrdinalIgnoreCase);
        foreach (string rel in relFiles)
        {
            string full = Path.Combine(root, rel);
            string text = File.ReadAllText(full, Encoding.UTF8);
            parsed[rel] = (MarkdownHistoryParser.Parse(text, rel, File.GetLastWriteTimeUtc(full), knownLabels), Sha256File(full));
        }

        List<HistoryProposal> result = new();
        HashSet<string> consumed = new(StringComparer.OrdinalIgnoreCase);

        // Prima le patch che citano un documento: si fondono. (Copia: il documento
        // citato puo' entrare in parsed anche se fuori dai glob.)
        foreach ((string rel, (DraftChange draft, string sha)) in parsed.ToList())
        {
            if (draft.CitedDocument == null)
            {
                continue;
            }

            string? docRel = parsed.Keys.FirstOrDefault(k => k.Equals(draft.CitedDocument, StringComparison.OrdinalIgnoreCase));
            if (docRel == null && File.Exists(Path.Combine(root, draft.CitedDocument)))
            {
                docRel = draft.CitedDocument;
                string full = Path.Combine(root, docRel);
                parsed.TryAdd(docRel, (MarkdownHistoryParser.Parse(File.ReadAllText(full, Encoding.UTF8), docRel, File.GetLastWriteTimeUtc(full), knownLabels), Sha256File(full)));
            }

            if (docRel == null || consumed.Contains(docRel))
            {
                continue;
            }

            HistoryProposal p = new() { Draft = MarkdownHistoryParser.Merge(parsed[docRel].Draft, draft) };
            p.Sources.Add((docRel, parsed[docRel].Sha));
            p.Sources.Add((rel, sha));
            p.AlreadyImported = p.Sources.All(s => isImported(s.Sha256));
            result.Add(p);
            consumed.Add(docRel);
            consumed.Add(rel);
        }

        foreach ((string rel, (DraftChange draft, string sha)) in parsed)
        {
            if (consumed.Contains(rel))
            {
                continue;
            }

            HistoryProposal p = new() { Draft = draft };
            p.Sources.Add((rel, sha));
            p.AlreadyImported = isImported(sha);
            result.Add(p);
        }

        return result.OrderByDescending(p => p.Draft.Date).ThenBy(p => p.Draft.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
