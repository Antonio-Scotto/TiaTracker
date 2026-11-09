using Microsoft.Data.Sqlite;
using TiaTracker.Core.Domain;

namespace TiaTracker.Data;

public sealed class ChangeRepository
{
    private readonly Db _db;

    public ChangeRepository(Db db)
    {
        _db = db;
    }

    /// <summary>Modifiche di una commessa con blocchi e stati per versione, piu' recenti prima.</summary>
    public List<Change> ByCommessa(long commessaId, bool includeDrafts = true)
    {
        List<Change> changes = _db.Query(
            "SELECT * FROM change WHERE commessa_id = $c" + (includeDrafts ? "" : " AND draft = 0") +
            " ORDER BY COALESCE(change_date, substr(created_utc, 1, 10)) DESC, id DESC",
            Map, ("$c", commessaId));
        Attach(changes, "commessa_id = $c", ("$c", commessaId));
        return changes;
    }

    public Change? Get(long id)
    {
        List<Change> changes = _db.Query("SELECT * FROM change WHERE id = $id", Map, ("$id", id));
        Attach(changes, "id = $id", ("$id", id));
        return changes.FirstOrDefault();
    }

    private void Attach(List<Change> changes, string filter, (string, object?) arg)
    {
        Dictionary<long, Change> byId = changes.ToDictionary(c => c.Id);
        if (byId.Count == 0)
        {
            return;
        }

        foreach ((long changeId, ChangeBlock block) in _db.Query(
                     "SELECT * FROM change_block WHERE change_id IN (SELECT id FROM change WHERE " + filter + ") ORDER BY block_name",
                     r => (r.L("change_id"), new ChangeBlock
                     {
                         BlockName = r.S("block_name"),
                         Family = r.Str("family"),
                         Action = r.Str("action"),
                         Note = r.Str("note"),
                     }), arg))
        {
            if (byId.TryGetValue(changeId, out Change? c))
            {
                c.Blocks.Add(block);
            }
        }

        foreach (ChangeVersion cv in _db.Query(
                     "SELECT * FROM change_version WHERE change_id IN (SELECT id FROM change WHERE " + filter + ")",
                     MapVersion, arg))
        {
            if (byId.TryGetValue(cv.ChangeId, out Change? c))
            {
                c.Versions.Add(cv);
            }
        }
    }

    /// <summary>
    /// Inserisce o aggiorna la modifica con i suoi blocchi e stati. Ogni
    /// cambio di stato per versione finisce in change_event.
    /// </summary>
    public long Save(Change c, string? eventDetail = null)
    {
        DateTime now = DateTime.UtcNow;
        Change? old = c.Id == 0 ? null : Get(c.Id);

        using Db.Tx tx = _db.Begin();
        (string, object?)[] args =
        {
            ("$c", c.CommessaId), ("$t", c.Title.Trim()), ("$d", Blank(c.Description)), ("$m", Blank(c.Motivation)),
            ("$date", c.Date), ("$s", ChangeStates.ScopeToDb(c.Scope)), ("$draft", c.Draft), ("$conf", c.Confidence),
            ("$ex", c.Excerpt), ("$src", c.ImportSourceId), ("$now", now), ("$id", c.Id),
        };

        if (old == null)
        {
            c.CreatedUtc = now;
            c.Id = _db.Insert(
                @"INSERT INTO change (commessa_id, title, description, motivation, change_date, scope, draft, confidence, excerpt,
                    import_source_id, created_utc, updated_utc)
                  VALUES ($c, $t, $d, $m, $date, $s, $draft, $conf, $ex, $src, $now, $now)", args);
            AddEvent(c.Id, null, c.Draft ? "bozza" : "creata", null, null, eventDetail, now);
        }
        else
        {
            _db.Exec(
                @"UPDATE change SET title = $t, description = $d, motivation = $m, change_date = $date, scope = $s, draft = $draft,
                    confidence = $conf, excerpt = $ex, import_source_id = $src, updated_utc = $now WHERE id = $id", args);
            if (old.Draft && !c.Draft)
            {
                AddEvent(c.Id, null, "confermata", null, null, eventDetail, now);
            }
        }

        c.UpdatedUtc = now;

        _db.Exec("DELETE FROM change_block WHERE change_id = $id", ("$id", c.Id));
        foreach (ChangeBlock b in c.Blocks.Where(b => !string.IsNullOrWhiteSpace(b.BlockName))
                     .GroupBy(b => b.BlockName.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            _db.Exec("INSERT INTO change_block (change_id, block_name, family, action, note) VALUES ($id, $n, $f, $a, $note)",
                ("$id", c.Id), ("$n", b.BlockName.Trim()), ("$f", b.Family), ("$a", Blank(b.Action)), ("$note", Blank(b.Note)));
        }

        Dictionary<long, ChangeVersion> before = old?.Versions.ToDictionary(v => v.VersionId) ?? new();
        HashSet<long> keep = new();
        foreach (ChangeVersion cv in c.Versions)
        {
            cv.ChangeId = c.Id;
            keep.Add(cv.VersionId);
            before.TryGetValue(cv.VersionId, out ChangeVersion? prev);
            if (prev == null || prev.State != cv.State)
            {
                cv.StateUtc ??= now;
                AddEvent(c.Id, cv.VersionId, "stato", prev == null ? null : ChangeStates.ToDb(prev.State),
                    ChangeStates.ToDb(cv.State), cv.Note, now);
            }

            UpsertVersion(cv);
        }

        foreach (ChangeVersion prev in before.Values.Where(v => !keep.Contains(v.VersionId)))
        {
            _db.Exec("DELETE FROM change_version WHERE change_id = $c AND version_id = $v", ("$c", c.Id), ("$v", prev.VersionId));
            AddEvent(c.Id, prev.VersionId, "rimossa-da-versione", ChangeStates.ToDb(prev.State), null, null, now);
        }

        tx.Commit();
        return c.Id;
    }

    private void UpsertVersion(ChangeVersion cv)
    {
        _db.Exec(
            @"INSERT INTO change_version (change_id, version_id, state, state_utc, errors, warnings, note, evidence_run_id)
              VALUES ($c, $v, $s, $u, $e, $w, $n, $r)
              ON CONFLICT (change_id, version_id) DO UPDATE SET state = $s, state_utc = $u, errors = $e, warnings = $w,
                note = $n, evidence_run_id = $r",
            ("$c", cv.ChangeId), ("$v", cv.VersionId), ("$s", ChangeStates.ToDb(cv.State)), ("$u", cv.StateUtc),
            ("$e", cv.Errors), ("$w", cv.Warnings), ("$n", Blank(cv.Note)), ("$r", cv.EvidenceRunId));
    }

    /// <summary>Cambia lo stato di una sola cella della matrice.</summary>
    public void SetVersionState(long changeId, long versionId, ChangeState state, string? note = null, long? evidenceRunId = null)
    {
        DateTime now = DateTime.UtcNow;
        ChangeVersion? prev = _db.Query("SELECT * FROM change_version WHERE change_id = $c AND version_id = $v", MapVersion,
            ("$c", changeId), ("$v", versionId)).FirstOrDefault();

        using Db.Tx tx = _db.Begin();
        ChangeVersion cv = prev ?? new ChangeVersion { ChangeId = changeId, VersionId = versionId };
        if (prev == null || prev.State != state)
        {
            AddEvent(changeId, versionId, "stato", prev == null ? null : ChangeStates.ToDb(prev.State), ChangeStates.ToDb(state), note, now);
            cv.StateUtc = now;
        }

        cv.State = state;
        if (note != null)
        {
            cv.Note = note;
        }

        if (evidenceRunId != null)
        {
            cv.EvidenceRunId = evidenceRunId;
        }

        UpsertVersion(cv);
        _db.Exec("UPDATE change SET updated_utc = $now WHERE id = $id", ("$now", now), ("$id", changeId));
        tx.Commit();
    }

    public void RemoveFromVersion(long changeId, long versionId)
    {
        using Db.Tx tx = _db.Begin();
        _db.Exec("DELETE FROM change_version WHERE change_id = $c AND version_id = $v", ("$c", changeId), ("$v", versionId));
        AddEvent(changeId, versionId, "rimossa-da-versione", null, null, null, DateTime.UtcNow);
        tx.Commit();
    }

    public void Delete(long changeId)
    {
        _db.Exec("DELETE FROM change WHERE id = $id", ("$id", changeId));
    }

    public List<ChangeEvent> Events(long changeId) =>
        _db.Query("SELECT * FROM change_event WHERE change_id = $c ORDER BY utc DESC, id DESC", r => new ChangeEvent
        {
            Id = r.L("id"),
            ChangeId = r.L("change_id"),
            VersionId = r.LN("version_id"),
            Utc = r.Utc("utc") ?? default,
            Kind = r.S("kind"),
            OldState = r.Str("old_state"),
            NewState = r.Str("new_state"),
            Detail = r.Str("detail"),
        }, ("$c", changeId));

    private void AddEvent(long changeId, long? versionId, string kind, string? oldState, string? newState, string? detail, DateTime utc)
    {
        _db.Exec(
            "INSERT INTO change_event (change_id, version_id, utc, kind, old_state, new_state, detail) VALUES ($c, $v, $u, $k, $o, $n, $d)",
            ("$c", changeId), ("$v", versionId), ("$u", utc), ("$k", kind), ("$o", oldState), ("$n", newState), ("$d", detail));
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static Change Map(SqliteDataReader r) => new()
    {
        Id = r.L("id"),
        CommessaId = r.L("commessa_id"),
        Title = r.S("title"),
        Description = r.Str("description"),
        Motivation = r.Str("motivation"),
        Date = r.Date("change_date"),
        Scope = ChangeStates.ScopeFromDb(r.Str("scope")),
        Draft = r.B("draft"),
        Confidence = r.Str("confidence"),
        Excerpt = r.Str("excerpt"),
        ImportSourceId = r.LN("import_source_id"),
        CreatedUtc = r.Utc("created_utc") ?? default,
        UpdatedUtc = r.Utc("updated_utc") ?? default,
    };

    private static ChangeVersion MapVersion(SqliteDataReader r) => new()
    {
        ChangeId = r.L("change_id"),
        VersionId = r.L("version_id"),
        State = ChangeStates.FromDb(r.Str("state")),
        StateUtc = r.Utc("state_utc"),
        Errors = r.IN("errors"),
        Warnings = r.IN("warnings"),
        Note = r.Str("note"),
        EvidenceRunId = r.LN("evidence_run_id"),
    };
}
