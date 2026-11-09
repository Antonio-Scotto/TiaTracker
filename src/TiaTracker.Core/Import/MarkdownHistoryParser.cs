using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TiaTracker.Core.Domain;

namespace TiaTracker.Core.Import;

/// <summary>Stato di una versione dedotto da un documento, con la riga che lo prova.</summary>
public sealed class VersionEvidence
{
    public string Label { get; init; } = "";
    public ChangeState State { get; set; } = ChangeState.Planned;
    public int? Errors { get; set; }
    public int? Warnings { get; set; }
    public bool NotSaved { get; set; }
    public string? Line { get; set; }

    /// <summary>Prova di stato, o versione nominata in un campo Progetto o in una riga STATO.</summary>
    public bool Strong { get; set; }
}

public sealed class DraftBlock
{
    public string Name { get; init; } = "";
    public string? Action { get; set; }
}

/// <summary>Una modifica proposta da un documento storico, da far confermare all'utente.</summary>
public sealed class DraftChange
{
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public DateOnly? Date { get; set; }
    public string DateSource { get; set; } = "";
    public List<DraftBlock> Blocks { get; } = new();
    public List<VersionEvidence> Versions { get; } = new();
    public string Confidence { get; set; } = "bassa";
    public string? Excerpt { get; set; }
    public ChangeScope Scope { get; set; } = ChangeScope.Plc;
    public List<string> SourceFiles { get; } = new();

    /// <summary>Documento citato (es. doc\Modifiche_X.md) con cui fondersi.</summary>
    public string? CitedDocument { get; set; }
}

/// <summary>
/// Estrazione deterministica (regex, niente LLM) di modifiche dai README,
/// LEGGIMI e documenti di analisi. Tutto cio' che esce e' una bozza.
/// </summary>
public static class MarkdownHistoryParser
{
    private static readonly Regex H1 = new(@"^#\s+(.+?)\s*$", RegexOptions.Multiline);
    private static readonly Regex DataField = new(@"^\*\*Data:\*\*\s*(\d{1,2})/(\d{1,2})/(\d{4})", RegexOptions.Multiline);
    private static readonly Regex FolderDate = new(@"_(20\d{2})(\d{2})(\d{2})(?:_|$)");
    private static readonly Regex StatusDate = new(@"STATO\W{0,6}(?:—|-|:)?\s*(\d{1,2})/(\d{1,2})/(\d{4})", RegexOptions.IgnoreCase);
    private static readonly Regex BlocksField = new(@"^\*\*Blocchi[^*]*:\*\*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private static readonly Regex ProjectField = new(@"^\*\*Progetto([^*]*):\*\*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private static readonly Regex Backticked = new(@"`([^`\r\n]+)`");
    private static readonly Regex SourceFile = new(@"^(?:\d+_)?(?<name>[A-Za-z][A-Za-z0-9_\-]*?)\.(?:scl|db|udt|awl|xml)$", RegexOptions.IgnoreCase);
    private static readonly Regex NumberedFile = new(@"^\d+_[A-Za-z][A-Za-z0-9_\-]*\.(?:scl|db|udt|awl)$", RegexOptions.IgnoreCase);
    private static readonly Regex Identifier = new(@"^[A-Za-z][A-Za-z0-9_]*_(?:FB|FC|OB|DB|IDB|UDT)$");
    private static readonly Regex VersionMention = new(@"(?<![\w.])V?(\d{1,2})\.(\d{1,3})(?![\d.])", RegexOptions.IgnoreCase);
    private static readonly Regex Errors = new(@"(\d+)\s*errori", RegexOptions.IgnoreCase);
    private static readonly Regex Warnings = new(@"(\d+)\s*warning", RegexOptions.IgnoreCase);
    private static readonly Regex DocCitation = new(@"`(doc[\\/][^`]+?\.md)`", RegexOptions.IgnoreCase);

    public static DraftChange Parse(string text, string relPath, DateTime fileMtimeUtc, IReadOnlyCollection<string> knownLabels)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        DraftChange d = new();
        d.SourceFiles.Add(relPath);

        Match h1 = H1.Match(text);
        d.Title = h1.Success ? Clean(h1.Groups[1].Value) : Path.GetFileNameWithoutExtension(relPath);
        if (relPath.Contains("Ignition", StringComparison.OrdinalIgnoreCase) || d.Title.Contains("Ignition", StringComparison.OrdinalIgnoreCase))
        {
            d.Scope = ChangeScope.Ignition;
        }

        ParseDate(d, text, relPath, fileMtimeUtc);
        ParseBlocks(d, text);
        ParseVersions(d, text, knownLabels);
        d.Description = FirstParagraph(text);
        d.Excerpt = StatusExcerpt(text) ?? d.Description;

        Match cited = DocCitation.Match(text);
        if (cited.Success && !relPath.StartsWith("doc", StringComparison.OrdinalIgnoreCase))
        {
            d.CitedDocument = cited.Groups[1].Value.Replace('/', '\\');
        }

        d.Confidence = ConfidenceOf(d);
        return d;
    }

    private static void ParseDate(DraftChange d, string text, string relPath, DateTime mtime)
    {
        Match m = DataField.Match(text);
        if (m.Success && TryDate(m.Groups[3].Value, m.Groups[2].Value, m.Groups[1].Value, out DateOnly a))
        {
            d.Date = a;
            d.DateSource = "campo Data";
            return;
        }

        string folder = Path.GetFileName(Path.GetDirectoryName(relPath) ?? "") ?? "";
        m = FolderDate.Match(folder);
        if (m.Success && TryDate(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, out DateOnly b))
        {
            d.Date = b;
            d.DateSource = "nome cartella";
            return;
        }

        m = StatusDate.Match(text);
        if (m.Success && TryDate(m.Groups[3].Value, m.Groups[2].Value, m.Groups[1].Value, out DateOnly c))
        {
            d.Date = c;
            d.DateSource = "riga STATO";
            return;
        }

        d.Date = DateOnly.FromDateTime(mtime.ToLocalTime());
        d.DateSource = "data del file";
    }

    private static void ParseBlocks(DraftChange d, string text)
    {
        void Add(string name, string? action)
        {
            name = name.Trim();
            if (name.Length == 0)
            {
                return;
            }

            DraftBlock? existing = d.Blocks.FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                d.Blocks.Add(new DraftBlock { Name = name, Action = action });
            }
            else if (existing.Action == null && action != null)
            {
                existing.Action = action;
            }
        }

        foreach (Match m in BlocksField.Matches(text))
        {
            foreach (Match t in Backticked.Matches(m.Groups[1].Value))
            {
                string v = t.Groups[1].Value;
                Match f = SourceFile.Match(v);
                Add(f.Success ? f.Groups["name"].Value : v, null);
            }
        }

        // Righe di tabella con un file sorgente e una colonna Azione.
        string[] lines = text.Split('\n');
        int actionCol = -1;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (!line.StartsWith('|'))
            {
                actionCol = -1;
                continue;
            }

            string[] cells = Cells(line);
            int header = Array.FindIndex(cells, c => c.Equals("Azione", StringComparison.OrdinalIgnoreCase));
            if (header >= 0)
            {
                actionCol = header;
                continue;
            }

            foreach (Match t in Backticked.Matches(line))
            {
                if (!NumberedFile.IsMatch(t.Groups[1].Value))
                {
                    continue;
                }

                string? action = actionCol >= 0 && actionCol < cells.Length ? Action(cells[actionCol]) : null;
                Add(SourceFile.Match(t.Groups[1].Value).Groups["name"].Value, action);
            }
        }

        // File numerati citati in elenchi ("1. `1_ST030_FilterBooking_DB.db`: ...").
        foreach (string raw in lines)
        {
            string line = raw.TrimStart();
            if (!Regex.IsMatch(line, @"^(\d+\.|[-*])\s"))
            {
                continue;
            }

            Match t = Backticked.Match(line);
            if (t.Success && NumberedFile.IsMatch(t.Groups[1].Value) && t.Index < 12)
            {
                Add(SourceFile.Match(t.Groups[1].Value).Groups["name"].Value, null);
            }
        }
    }

    private static string? Action(string cell)
    {
        string c = cell.Replace("*", "", StringComparison.Ordinal).Trim().ToLowerInvariant();
        return c.StartsWith("nuov", StringComparison.Ordinal) ? "nuovo"
            : c.StartsWith("sovrascr", StringComparison.Ordinal) ? "sovrascrive"
            : c.StartsWith("modific", StringComparison.Ordinal) ? "modifica"
            : c.StartsWith("elimin", StringComparison.Ordinal) || c.StartsWith("cancell", StringComparison.Ordinal) ? "elimina"
            : null;
    }

    private static void ParseVersions(DraftChange d, string text, IReadOnlyCollection<string> knownLabels)
    {
        HashSet<string> known = new(knownLabels, StringComparer.OrdinalIgnoreCase);
        string[] lines = text.Split('\n');
        string[]? tableHeader = null;

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.StartsWith('|'))
            {
                string[] cells = Cells(line);
                if (cells.Any(c => c.Equals("Progetto", StringComparison.OrdinalIgnoreCase) || c.Equals("Salvato", StringComparison.OrdinalIgnoreCase)))
                {
                    tableHeader = cells;
                    continue;
                }
            }
            else
            {
                tableHeader = null;
            }

            // "Progetto esaminato" e "Base: ..." descrivono la base dell'analisi, non
            // dove va la modifica (e un "salvato alle 14:10" li' non riguarda la modifica).
            Match pf = ProjectField.Match(line);
            if (pf.Success && pf.Groups[1].Value.Contains("esaminat", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Regex.IsMatch(line, @"^\W*base\b", RegexOptions.IgnoreCase))
            {
                continue;
            }

            List<string> labels = VersionMention.Matches(line)
                .Select(m => "V" + m.Groups[1].Value + "." + m.Groups[2].Value)
                .Where(known.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (labels.Count == 0)
            {
                continue;
            }

            (ChangeState state, bool notSaved, int? errors, int? warnings) = Evidence(line, tableHeader);
            bool strong = state != ChangeState.Planned || notSaved || pf.Success || line.Contains("STATO", StringComparison.Ordinal);
            foreach (string label in labels)
            {
                string canonical = known.First(k => k.Equals(label, StringComparison.OrdinalIgnoreCase));
                VersionEvidence? ev = d.Versions.FirstOrDefault(v => v.Label == canonical);
                if (ev == null)
                {
                    ev = new VersionEvidence { Label = canonical };
                    d.Versions.Add(ev);
                }

                ev.Strong |= strong;

                if (Rank(state) > Rank(ev.State) || (state == ev.State && ev.Line == null))
                {
                    ev.State = state;
                    ev.NotSaved = notSaved;
                    ev.Errors = errors ?? ev.Errors;
                    ev.Warnings = warnings ?? ev.Warnings;
                    ev.Line = Shorten(line);
                }
                else if (state == ev.State)
                {
                    ev.NotSaved |= notSaved;
                    ev.Errors ??= errors;
                    ev.Warnings ??= warnings;
                }
            }
        }

        // Versioni solo citate nel testo ("come nel 0.67...") non sono destinazioni della modifica.
        if (d.Versions.Any(v => v.Strong))
        {
            d.Versions.RemoveAll(v => !v.Strong);
        }
    }

    /// <summary>Lo stato piu' forte che una riga dimostra per le versioni che nomina.</summary>
    internal static (ChangeState State, bool NotSaved, int? Errors, int? Warnings) Evidence(string line, string[]? tableHeader)
    {
        string l = line.ToLowerInvariant().Replace("*", "", StringComparison.Ordinal);

        // Istruzioni ("va salvato prima di importare", "da compilare") non sono prove.
        l = Regex.Replace(l, @"\b(?:va|vanno|deve|devono|dovra'?|da|bisogna|occorre)\s+(?:essere\s+|ancora\s+)?(?:salvat|compilat|importat|salvar|compilar|importar)\w*", " ");
        const string Neg = @"\bnon\s+(?:(?:è|e'|e|era|viene|e'\s+stato|è\s+stato|ancora|piu'|più)\s+)*";
        bool notSaved = Regex.IsMatch(l, Neg + @"salvat");
        bool saved = !notSaved && Regex.IsMatch(l, @"\bsalvat[oaie]\b");
        bool compiled = !Regex.IsMatch(l, Neg + @"compilat") && Regex.IsMatch(l, @"\bcompila(?:t[oaie]\b|zione)");
        bool notImported = Regex.IsMatch(l, Neg + @"importat");
        bool imported = !notImported && Regex.IsMatch(l, @"\bimportat[oaie]\b");

        if (tableHeader != null && line.TrimStart().StartsWith('|'))
        {
            string[] cells = Cells(line);
            int savedCol = Array.FindIndex(tableHeader, c => c.Equals("Salvato", StringComparison.OrdinalIgnoreCase));
            if (savedCol >= 0 && savedCol < cells.Length)
            {
                string v = cells[savedCol].Replace("*", "", StringComparison.Ordinal).Trim().ToLowerInvariant();
                if (v is "no" or "non salvato")
                {
                    notSaved = true;
                    saved = false;
                }
                else if (v is "si" or "sì" or "si'" or "yes" or "salvato")
                {
                    saved = true;
                }
            }

            // Celle "ok, 1 warning" = compilato.
            if (cells.Any(c => Regex.IsMatch(c, @"^\s*ok\b", RegexOptions.IgnoreCase) || Warnings.IsMatch(c) || Errors.IsMatch(c)))
            {
                compiled = true;
            }
        }

        int? errors = Sum(Errors, l);
        int? warnings = Sum(Warnings, l);
        if (errors == null && compiled && Regex.IsMatch(l, @"\bok\b"))
        {
            errors = 0;
        }

        ChangeState state = saved ? ChangeState.Saved
            : compiled ? ChangeState.Compiled
            : imported ? ChangeState.Imported
            : ChangeState.Planned;
        return (state, notSaved && state != ChangeState.Saved, errors, warnings);
    }

    private static int? Sum(Regex r, string s)
    {
        MatchCollection ms = r.Matches(s);
        return ms.Count == 0 ? null : ms.Sum(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    private static int Rank(ChangeState s) => s switch
    {
        ChangeState.Saved => 3,
        ChangeState.Compiled => 2,
        ChangeState.Imported => 1,
        _ => 0,
    };

    private static string ConfidenceOf(DraftChange d)
    {
        bool hasBlocks = d.Blocks.Count > 0;
        bool hasEvidence = d.Versions.Any(v => v.State != ChangeState.Planned || v.Line != null);
        return hasBlocks && hasEvidence && d.DateSource != "data del file" ? "alta"
            : hasBlocks || d.Versions.Count > 0 ? "media"
            : "bassa";
    }

    /// <summary>Primo paragrafo di testo dopo titolo e campi d'intestazione.</summary>
    private static string? FirstParagraph(string text)
    {
        StringBuilder sb = new();
        bool started = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('#') || line.StartsWith("**", StringComparison.Ordinal) && line.Contains(":**", StringComparison.Ordinal) ||
                line.StartsWith('>') || line.StartsWith('|') || line == "---" || line.StartsWith("```", StringComparison.Ordinal))
            {
                if (started)
                {
                    break;
                }

                continue;
            }

            if (line.Length == 0)
            {
                if (started)
                {
                    break;
                }

                continue;
            }

            started = true;
            sb.Append(sb.Length > 0 ? " " : "").Append(line);
            if (sb.Length > 700)
            {
                break;
            }
        }

        return sb.Length == 0 ? null : Clean(sb.ToString());
    }

    /// <summary>La riga STATO (anche in blockquote) e quelle che la seguono nello stesso blocco.</summary>
    private static string? StatusExcerpt(string text)
    {
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (!Regex.IsMatch(lines[i], @"STATO", RegexOptions.None))
            {
                continue;
            }

            StringBuilder sb = new(lines[i].TrimStart('>', ' '));
            for (int k = i + 1; k < lines.Length && lines[k].TrimStart().StartsWith('>'); k++)
            {
                string next = lines[k].TrimStart('>', ' ');
                if (next.Length > 0)
                {
                    sb.Append('\n').Append(next);
                }
            }

            return sb.ToString().Trim();
        }

        return null;
    }

    private static string[] Cells(string line) =>
        line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();

    private static string Clean(string s) => s.Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal).Trim();

    private static string Shorten(string s) => s.Length <= 300 ? s : s[..300] + "...";

    private static bool TryDate(string y, string m, string d, out DateOnly date)
    {
        date = default;
        int yy = int.Parse(y, CultureInfo.InvariantCulture), mm = int.Parse(m, CultureInfo.InvariantCulture), dd = int.Parse(d, CultureInfo.InvariantCulture);
        if (mm is < 1 or > 12 || dd < 1 || dd > DateTime.DaysInMonth(yy, mm))
        {
            return false;
        }

        date = new DateOnly(yy, mm, dd);
        return true;
    }

    /// <summary>
    /// Fonde il LEGGIMI/README di una patch con il documento che cita: titolo
    /// e descrizione dal documento, blocchi e stati da entrambi (vince lo stato
    /// piu' forte), data piu' recente.
    /// </summary>
    public static DraftChange Merge(DraftChange doc, DraftChange readme)
    {
        DraftChange m = new()
        {
            Title = doc.Title,
            Description = doc.Description ?? readme.Description,
            Date = new[] { doc.Date, readme.Date }.Max(),
            DateSource = doc.Date >= readme.Date ? doc.DateSource : readme.DateSource,
            Excerpt = string.Join("\n\n", new[] { readme.Excerpt, doc.Excerpt }.Where(e => !string.IsNullOrWhiteSpace(e)).Distinct()),
            Scope = doc.Scope,
        };
        m.SourceFiles.AddRange(doc.SourceFiles);
        m.SourceFiles.AddRange(readme.SourceFiles);
        foreach (DraftBlock b in doc.Blocks.Concat(readme.Blocks))
        {
            DraftBlock? existing = m.Blocks.FirstOrDefault(x => x.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                m.Blocks.Add(new DraftBlock { Name = b.Name, Action = b.Action });
            }
            else
            {
                existing.Action ??= b.Action;
            }
        }

        // Prima il README della patch: a parita' di stato e' il piu' preciso.
        foreach (VersionEvidence v in readme.Versions.Concat(doc.Versions))
        {
            VersionEvidence? e = m.Versions.FirstOrDefault(x => x.Label == v.Label);
            if (e == null)
            {
                m.Versions.Add(new VersionEvidence
                {
                    Label = v.Label, State = v.State, NotSaved = v.NotSaved, Errors = v.Errors, Warnings = v.Warnings, Line = v.Line, Strong = v.Strong,
                });
                continue;
            }

            e.Strong |= v.Strong;
            if (Rank(v.State) > Rank(e.State))
            {
                e.State = v.State;
                e.NotSaved = v.NotSaved;
                e.Errors = v.Errors;
                e.Warnings = v.Warnings;
                e.Line = v.Line;
            }
            else if (v.State == e.State)
            {
                e.Errors ??= v.Errors;
                e.Warnings ??= v.Warnings;
                e.NotSaved |= v.NotSaved;
            }
        }

        if (m.Versions.Any(v => v.Strong))
        {
            m.Versions.RemoveAll(v => !v.Strong);
        }

        m.Confidence = ConfidenceOf(m);
        return m;
    }

    public static bool LooksLikeBlockName(string s) => Identifier.IsMatch(s);
}
