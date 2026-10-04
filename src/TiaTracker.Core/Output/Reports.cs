using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TiaTracker.Core.Compare;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Reconcile;

namespace TiaTracker.Core.Output;

/// <summary>Report leggibili scritti in TiaTrackerOut.</summary>
public static class Reports
{
    private static string Local(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    private static string Cell(string? s) => (s ?? "").Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    public static string Compare(string title, string summary, IReadOnlyList<CompareEntry> entries, ReconcileResult? reconcile)
    {
        StringBuilder sb = new();
        sb.AppendLine("# " + title).AppendLine();
        sb.AppendLine("Generato da TiaTracker il " + DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) + ".").AppendLine();
        sb.AppendLine(summary).AppendLine();

        List<CompareEntry> changed = entries.Where(e => e.IsChange).ToList();
        sb.AppendLine("## Differenze (" + changed.Count + ")").AppendLine();
        if (changed.Count > 0)
        {
            sb.AppendLine("| Stato | Blocco | Tipo | Cosa | Note |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (CompareEntry e in changed)
            {
                string note = e.ParentName != null ? "derivato da " + e.ParentName
                    : e.OldName != null ? "da " + e.OldName
                    : e.Units.Count > 0 ? string.Join("; ", e.Units.Select(u => "rete " + u.Index + " " + u.What))
                    : "";
                sb.AppendLine($"| {e.StatusText} | `{Cell(e.Name)}` | {e.Kind} | {Cell(e.FacetsText)} | {Cell(note)} |");
            }

            sb.AppendLine();
        }

        List<CompareEntry> other = entries.Where(e => e.Status is CompareStatus.Recompiled or CompareStatus.Reimported or CompareStatus.Unavailable).ToList();
        if (other.Count > 0)
        {
            sb.AppendLine("## Solo ricompilati, reimportati o non confrontabili (" + other.Count + ")").AppendLine();
            foreach (CompareEntry e in other)
            {
                sb.AppendLine($"- `{e.Name}`: {e.StatusText}" + (e.Note != null ? " (" + e.Note + ")" : ""));
            }

            sb.AppendLine();
        }

        if (reconcile != null)
        {
            sb.AppendLine("## Riconciliazione con le modifiche dichiarate").AppendLine();
            sb.AppendLine("| Esito | Blocco | Modifiche | Nota |");
            sb.AppendLine("|---|---|---|---|");
            foreach (ReconcileRow r in reconcile.Rows)
            {
                sb.AppendLine($"| {r.StatusText} | `{Cell(r.BlockName)}` | {Cell(r.ChangesText)} | {Cell(r.Note)} |");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <param name="loads">Carichi correnti sui PLC (null = dai flag storici della versione).</param>
    public static string ChangeRegisterMarkdown(Commessa c, IReadOnlyList<ProjectVersion> versions, IReadOnlyList<Change> changes,
        IReadOnlyList<VersionLoad>? loads = null)
    {
        Dictionary<long, ProjectVersion> byId = versions.ToDictionary(v => v.Id);
        StringBuilder sb = new();
        sb.AppendLine("# Registro modifiche - " + c.Display).AppendLine();
        sb.AppendLine("Generato da TiaTracker il " + DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) +
                      ". \"Compilata\" senza \"Salvata\" = progetto NON salvato in TIA.").AppendLine();

        sb.AppendLine("## Versioni").AppendLine();
        sb.AppendLine("| Versione | Progetto | Stato | Ultimo salvataggio | Note |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (ProjectVersion v in versions)
        {
            List<string> flags = new();
            if (v.State != VersionState.None)
            {
                flags.Add(VersionStates.Italian(v.State).ToLowerInvariant() + (v.StateNote != null ? " (" + v.StateNote + ")" : ""));
            }

            List<VersionLoad> mine = loads?.Where(l => l.VersionId == v.Id && l.IsCurrent).ToList() ?? new List<VersionLoad>();
            if (loads == null && v.IsLoaded)
            {
                flags.Add("sul PLC " + (v.LoadedPlc ?? "") + (v.LoadedUtc.HasValue ? " dal " + Local(v.LoadedUtc.Value) : ""));
            }

            foreach (VersionLoad l in mine)
            {
                flags.Add("sul " + (l.Plc.Length == 0 ? "PLC" : "PLC " + l.Plc) + " dal " + Local(l.LoadedUtc));
            }

            if (!v.HasFolder)
            {
                flags.Add("solo archivio");
            }

            sb.AppendLine($"| {v.Label} | `{Cell(v.ProjectName)}` | {string.Join(", ", flags)} | {(v.LastSavedUtc.HasValue ? Local(v.LastSavedUtc.Value) : "")} | {Cell(v.Note)} |");
        }

        sb.AppendLine().AppendLine("## Modifiche").AppendLine();
        foreach (Change ch in changes)
        {
            sb.AppendLine("### " + (ch.Date?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "") + " - " + ch.Title + (ch.Draft ? " (bozza)" : ""));
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(ch.Description))
            {
                sb.AppendLine(ch.Description).AppendLine();
            }

            if (ch.Blocks.Count > 0)
            {
                sb.AppendLine("Blocchi: " + string.Join(", ", ch.Blocks.Select(b => "`" + b.BlockName + "`" + (b.Action != null ? " (" + b.Action + ")" : ""))));
                sb.AppendLine();
            }

            foreach (ChangeVersion cv in ch.Versions.Where(x => byId.ContainsKey(x.VersionId)).OrderByDescending(x => byId[x.VersionId].SortKey, StringComparer.Ordinal))
            {
                sb.AppendLine("- " + byId[cv.VersionId].Label + ": **" + ChangeStates.Italian(cv.State) + "**" +
                              (cv.State is ChangeState.Imported or ChangeState.Compiled ? " (non salvata)" : "") +
                              (cv.Errors.HasValue || cv.Warnings.HasValue ? $", {cv.Errors ?? 0} errori / {cv.Warnings ?? 0} warning" : "") +
                              (cv.StateUtc.HasValue ? ", " + Local(cv.StateUtc.Value) : "") +
                              (string.IsNullOrWhiteSpace(cv.Note) ? "" : " - " + cv.Note));
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string ChangeRegisterCsv(IReadOnlyList<ProjectVersion> versions, IReadOnlyList<Change> changes)
    {
        List<ProjectVersion> cols = versions.Where(v => !v.Missing).ToList();
        StringBuilder sb = new();
        sb.AppendLine(string.Join(";", new[] { "Data", "Titolo", "Ambito", "Blocchi", "Bozza" }.Concat(cols.Select(v => v.Label)).Select(Quote)));
        foreach (Change ch in changes)
        {
            IEnumerable<string> cells = new[]
            {
                ch.Date?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "",
                ch.Title,
                ChangeStates.ScopeItalian(ch.Scope),
                string.Join(", ", ch.Blocks.Select(b => b.BlockName)),
                ch.Draft ? "si" : "",
            }.Concat(cols.Select(v =>
            {
                ChangeVersion? cv = ch.Versions.FirstOrDefault(x => x.VersionId == v.Id);
                return cv == null ? "" : ChangeStates.Italian(cv.State);
            }));
            sb.AppendLine(string.Join(";", cells.Select(Quote)));
        }

        return sb.ToString();
    }

    /// <summary>I dati della commessa in un file leggibile, accanto ai progetti.</summary>
    public static string CommessaJson(Commessa c, IReadOnlyList<ProjectVersion> versions, IReadOnlyList<Change> changes, IReadOnlyList<IpDevice> ip,
        IReadOnlyList<VersionLoad>? loads = null)
    {
        Dictionary<long, string> labels = versions.ToDictionary(v => v.Id, v => v.Label);
        var doc = new
        {
            formato = 1,
            generato = DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
            commessa = new { c.Code, c.Name, c.Customer, c.Note },
            versioni = versions.Select(v => new
            {
                v.Label, v.ProjectName, v.TiaVersionText, v.FolderRel, v.RarRel, v.BackupRel, v.LastSavedUtc,
                caricata = v.IsLoaded, v.LoadedUtc, v.LoadedPlc, v.LoadedNote, attiva = v.IsActive, archiviata = v.IsArchived, v.Note,
                stato = VersionStates.ToDb(v.State), notaStato = v.StateNote, statoDal = v.StateUtc,
                carichi = loads?.Where(l => l.VersionId == v.Id && l.IsCurrent)
                    .Select(l => new { plc = l.Plc.Length == 0 ? null : l.Plc, dal = l.LoadedUtc, nota = l.Note }).ToList(),
            }),
            modifiche = changes.Select(ch => new
            {
                ch.Title, ch.Description, ch.Motivation, data = ch.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ambito = ChangeStates.ScopeToDb(ch.Scope), bozza = ch.Draft,
                blocchi = ch.Blocks.Select(b => new { b.BlockName, b.Action, b.Note }),
                stati = ch.Versions.Where(v => labels.ContainsKey(v.VersionId)).Select(v => new
                {
                    versione = labels[v.VersionId], stato = ChangeStates.ToDb(v.State), v.StateUtc, v.Errors, v.Warnings, v.Note,
                }),
            }),
            rete = ip.Select(d => new { d.Network, d.Ip, d.Subnet, d.Gateway, d.Name, d.Kind, d.ProfinetName, d.Location, d.Mac, d.Notes }),
        };

        return JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
    }

    private static string Quote(string? s) => Csv.Quote(s);
}
