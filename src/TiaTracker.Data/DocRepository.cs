using System.Text.Json;
using Microsoft.Data.Sqlite;
using TiaTracker.Core.Documents;

namespace TiaTracker.Data;

/// <summary>Una generazione di documenti (per la freschezza e lo storico).</summary>
public sealed class DocExport
{
    public long Id { get; set; }
    public long CommessaId { get; set; }
    public string ListKind { get; set; } = "";
    public long? VersionId { get; set; }
    public long? HwSnapshotId { get; set; }
    public DateTime GeneratedUtc { get; set; }
    public string Formats { get; set; } = "";
    public List<string> Files { get; set; } = new();
}

/// <summary>Note per riga e righe manuali dei documenti, storico delle generazioni.</summary>
public sealed class DocRepository
{
    private readonly Db _db;

    public DocRepository(Db db)
    {
        _db = db;
    }

    // ---------- note ----------

    /// <summary>Tutte le note della commessa: tipo di documento → chiave di riga → nota.</summary>
    public Dictionary<string, IReadOnlyDictionary<string, string>> Notes(long commessaId)
    {
        Dictionary<string, Dictionary<string, string>> all = new(StringComparer.Ordinal);
        foreach ((string kind, string key, string note) in _db.Query(
                     "SELECT list_kind, row_key, note FROM doc_note WHERE commessa_id = $c",
                     r => (r.S("list_kind"), r.S("row_key"), r.S("note")), ("$c", commessaId)))
        {
            if (!all.TryGetValue(kind, out Dictionary<string, string>? map))
            {
                all[kind] = map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            map[key] = note;
        }

        return all.ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, string>)p.Value, StringComparer.Ordinal);
    }

    /// <summary>Scrive o toglie (testo vuoto) la nota di una riga.</summary>
    public void SetNote(long commessaId, string kind, string rowKey, string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            _db.Exec("DELETE FROM doc_note WHERE commessa_id = $c AND list_kind = $k AND row_key = $r",
                ("$c", commessaId), ("$k", kind), ("$r", rowKey));
            return;
        }

        _db.Exec(
            @"INSERT INTO doc_note (commessa_id, list_kind, row_key, note, updated_utc) VALUES ($c, $k, $r, $n, $u)
              ON CONFLICT (commessa_id, list_kind, row_key) DO UPDATE SET note = excluded.note, updated_utc = excluded.updated_utc",
            ("$c", commessaId), ("$k", kind), ("$r", rowKey), ("$n", note.Trim()), ("$u", DateTime.UtcNow));
    }

    // ---------- righe manuali ----------

    public List<DocManualRow> ManualRows(long commessaId) =>
        _db.Query("SELECT * FROM doc_manual_row WHERE commessa_id = $c ORDER BY list_kind, sort, id", MapRow, ("$c", commessaId));

    public long AddManualRow(long commessaId, string kind, IReadOnlyDictionary<string, string> cells)
    {
        long sort = Convert.ToInt64(_db.Scalar(
            "SELECT COALESCE(MAX(sort), 0) + 1 FROM doc_manual_row WHERE commessa_id = $c AND list_kind = $k",
            ("$c", commessaId), ("$k", kind)), System.Globalization.CultureInfo.InvariantCulture);
        return _db.Insert(
            "INSERT INTO doc_manual_row (commessa_id, list_kind, sort, cells_json, updated_utc) VALUES ($c, $k, $s, $j, $u)",
            ("$c", commessaId), ("$k", kind), ("$s", sort), ("$j", ToJson(cells)), ("$u", DateTime.UtcNow));
    }

    public void UpdateManualRow(long id, IReadOnlyDictionary<string, string> cells)
    {
        _db.Exec("UPDATE doc_manual_row SET cells_json = $j, updated_utc = $u WHERE id = $id",
            ("$id", id), ("$j", ToJson(cells)), ("$u", DateTime.UtcNow));
    }

    public void DeleteManualRow(long id)
    {
        _db.Exec("DELETE FROM doc_manual_row WHERE id = $id", ("$id", id));
    }

    /// <summary>Ultima modifica di note o righe manuali per tipo di documento (per "da rigenerare").</summary>
    public Dictionary<string, DateTime> LastEdits(long commessaId) =>
        _db.Query(
                @"SELECT list_kind, MAX(updated_utc) AS u FROM (
                    SELECT list_kind, updated_utc FROM doc_note WHERE commessa_id = $c
                    UNION ALL SELECT list_kind, updated_utc FROM doc_manual_row WHERE commessa_id = $c)
                  GROUP BY list_kind",
                r => (Kind: r.S("list_kind"), Utc: r.Utc("u")), ("$c", commessaId))
            .Where(x => x.Utc != null)
            .ToDictionary(x => x.Kind, x => x.Utc!.Value, StringComparer.Ordinal);

    // ---------- generazioni ----------

    public long RecordExport(DocExport e)
    {
        e.Id = _db.Insert(
            @"INSERT INTO doc_export (commessa_id, list_kind, version_id, hw_snapshot_id, generated_utc, formats, files_json)
              VALUES ($c, $k, $v, $h, $g, $f, $j)",
            ("$c", e.CommessaId), ("$k", e.ListKind), ("$v", e.VersionId), ("$h", e.HwSnapshotId), ("$g", e.GeneratedUtc),
            ("$f", e.Formats), ("$j", e.Files.Count == 0 ? null : JsonSerializer.Serialize(e.Files)));
        return e.Id;
    }

    /// <summary>L'ultima generazione di ogni tipo di documento della commessa.</summary>
    public Dictionary<string, DocExport> LatestExports(long commessaId) =>
        _db.Query("SELECT * FROM doc_export WHERE commessa_id = $c ORDER BY generated_utc DESC, id DESC", MapExport, ("$c", commessaId))
            .GroupBy(e => e.ListKind, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    public List<DocExport> Exports(long commessaId, int limit = 50) =>
        _db.Query("SELECT * FROM doc_export WHERE commessa_id = $c ORDER BY generated_utc DESC, id DESC LIMIT $n",
            MapExport, ("$c", commessaId), ("$n", limit));

    private static string ToJson(IReadOnlyDictionary<string, string> cells) =>
        JsonSerializer.Serialize(cells.Where(p => !string.IsNullOrWhiteSpace(p.Value)).ToDictionary(p => p.Key, p => p.Value.Trim()));

    private static DocManualRow MapRow(SqliteDataReader r)
    {
        Dictionary<string, string> cells = new(StringComparer.Ordinal);
        try
        {
            Dictionary<string, string>? parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(r.S("cells_json"));
            if (parsed != null)
            {
                foreach (KeyValuePair<string, string> p in parsed)
                {
                    cells[p.Key] = p.Value;
                }
            }
        }
        catch (JsonException)
        {
            // riga illeggibile: resta vuota, l'utente la vede e la corregge
        }

        return new DocManualRow
        {
            Id = r.L("id"),
            CommessaId = r.L("commessa_id"),
            ListKind = r.S("list_kind"),
            Sort = r.IN("sort") ?? 0,
            Cells = cells,
            UpdatedUtc = r.Utc("updated_utc") ?? default,
        };
    }

    private static DocExport MapExport(SqliteDataReader r)
    {
        List<string> files = new();
        string? json = r.Str("files_json");
        if (json != null)
        {
            try
            {
                files = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }
            catch (JsonException)
            {
            }
        }

        return new DocExport
        {
            Id = r.L("id"),
            CommessaId = r.L("commessa_id"),
            ListKind = r.S("list_kind"),
            VersionId = r.LN("version_id"),
            HwSnapshotId = r.LN("hw_snapshot_id"),
            GeneratedUtc = r.Utc("generated_utc") ?? default,
            Formats = r.S("formats"),
            Files = files,
        };
    }
}
