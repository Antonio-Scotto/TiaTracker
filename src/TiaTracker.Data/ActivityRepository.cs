using TiaTracker.Core.Planning;
using TiaTracker.Core.Status;

namespace TiaTracker.Data;

/// <summary>
/// Le attivita' recenti della commessa da tutte le storie: versioni (trovate,
/// stati, carichi), modifiche, pianificazione, snapshot, hardware e documenti.
/// </summary>
public sealed class ActivityRepository
{
    private readonly Db _db;

    public ActivityRepository(Db db)
    {
        _db = db;
    }

    public List<ActivityItem> Recent(long commessaId, int limit = 40) =>
        _db.Query(
            @"SELECT utc, cat, title, kind, field, value FROM (
                SELECT e.utc AS utc, 'versione' AS cat, v.label AS title, e.kind AS kind, NULL AS field, e.detail AS value
                  FROM version_event e JOIN version v ON v.id = e.version_id WHERE v.commessa_id = $c
                UNION ALL
                SELECT e.utc, 'modifica', ch.title, e.kind, NULL, COALESCE(e.new_state, e.detail)
                  FROM change_event e JOIN change ch ON ch.id = e.change_id WHERE ch.commessa_id = $c
                UNION ALL
                SELECT e.utc, 'piano', e.task_title, e.kind, e.field, e.new_value
                  FROM task_event e WHERE e.commessa_id = $c AND e.cascade = 0
                UNION ALL
                SELECT s.created_utc, 'snapshot', v.label, s.source, s.plc_name, CAST(s.item_count AS TEXT)
                  FROM snapshot s JOIN version v ON v.id = s.version_id WHERE v.commessa_id = $c
                UNION ALL
                SELECT h.created_utc, 'hardware', v.label, h.source, NULL,
                       h.device_count || ' dispositivi, ' || h.ip_count || ' IP, ' || h.io_tag_count || ' segnali I/O'
                  FROM hw_snapshot h JOIN version v ON v.id = h.version_id WHERE v.commessa_id = $c
                UNION ALL
                SELECT d.generated_utc, 'documenti', d.list_kind, NULL, NULL, d.formats
                  FROM doc_export d WHERE d.commessa_id = $c
              ) ORDER BY utc DESC LIMIT $n",
            r => Map(Db.ParseIso(r.Str("utc")) ?? default, r.S("cat"), r.S("title"), r.Str("kind"), r.Str("field"), r.Str("value")),
            ("$c", commessaId), ("$n", limit));

    /// <summary>Blocchi di uno snapshot per tipo (OB, FB, FC, GlobalDB, InstanceDB, UDT...).</summary>
    public Dictionary<string, int> BlockKinds(long snapshotId) =>
        _db.Query("SELECT kind, COUNT(*) AS n FROM snapshot_item WHERE snapshot_id = $s GROUP BY kind",
                r => (Kind: r.S("kind"), N: r.IN("n") ?? 0), ("$s", snapshotId))
            .ToDictionary(x => x.Kind, x => x.N, StringComparer.OrdinalIgnoreCase);

    private static ActivityItem Map(DateTime utc, string cat, string title, string? kind, string? field, string? value)
    {
        string? detail = cat switch
        {
            "versione" => kind switch
            {
                "trovata" => "trovata nella cartella",
                "stato" => "stato: " + value,
                "sparita" => "non piu' nella cartella",
                _ => value ?? kind,
            },
            "modifica" => kind switch
            {
                "creata" => "modifica registrata",
                "bozza" => "bozza importata dallo storico",
                "confermata" => "bozza confermata",
                "stato" => "stato " + StateText(value),
                _ => value ?? kind,
            },
            "piano" => kind switch
            {
                PlanEventKinds.Created => "attivita' creata",
                PlanEventKinds.Deleted => "attivita' eliminata",
                PlanEventKinds.Dates => "date " + PlanFields.Display(PlanFields.Dates, value),
                PlanEventKinds.Linked => "nuova dipendenza",
                PlanEventKinds.Unlinked => "dipendenza tolta",
                PlanEventKinds.Undone => "cambiamento annullato",
                _ => PlanFields.Label(field) + ": " + PlanFields.Display(field, value),
            },
            "snapshot" => "snapshot " + (kind == "attach" ? "da TIA aperto" : kind == "file" ? "dalla cartella" : "da " + kind) +
                          (field != null ? " · " + field : "") + " · " + value + " elementi",
            "hardware" => "hardware e rete letti: " + value,
            "documenti" => (value ?? "").ToUpperInvariant().Replace(",", ", ", StringComparison.Ordinal) + " generati",
            _ => value,
        };

        string shownTitle = cat == "documenti" ? Core.Documents.DocKinds.Title(title) : title;
        return new ActivityItem(utc, cat, shownTitle, detail);
    }

    private static string StateText(string? db) => db switch
    {
        "planned" => "Pianificata",
        "imported" => "Importata",
        "compiled" => "Compilata",
        "saved" => "Salvata",
        "dropped" => "Scartata",
        null => "",
        _ => db,
    };
}
