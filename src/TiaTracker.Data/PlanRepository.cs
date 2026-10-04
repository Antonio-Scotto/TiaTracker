using System.Globalization;
using Microsoft.Data.Sqlite;
using TiaTracker.Core.Planning;

namespace TiaTracker.Data;

/// <summary>Esito di un annullamento: riuscito o rifiutato con il motivo.</summary>
public sealed record PlanUndoResult(bool Ok, string Message, string? Batch = null);

/// <summary>
/// Task, dipendenze e storico della pianificazione. Ogni salvataggio scrive nello
/// storico solo i campi cambiati; le azioni (un trascinamento con la sua cascata)
/// hanno un lotto, che si puo' annullare finche' nessuno ha toccato di nuovo quei task.
/// </summary>
public sealed class PlanRepository
{
    private static readonly HashSet<string> Columns = new(StringComparer.Ordinal)
    {
        "title", "description", "status", "priority", "assignee", "kind", "parent_id", "locked", "progress",
        "estimated_hours", "actual_hours", "version_id",
    };

    private readonly Db _db;

    public PlanRepository(Db db)
    {
        _db = db;
    }

    public static string NewBatch() => Guid.NewGuid().ToString("N")[..12];

    // ---------- lettura ----------

    public List<PlanTask> Tasks(long commessaId) =>
        _db.Query("SELECT * FROM task WHERE commessa_id = $c ORDER BY sort, id", Map, ("$c", commessaId));

    public PlanTask? Get(long id) => _db.Query("SELECT * FROM task WHERE id = $id", Map, ("$id", id)).FirstOrDefault();

    public List<PlanLink> Links(long commessaId) =>
        _db.Query(
            @"SELECT l.* FROM task_link l JOIN task t ON t.id = l.successor_id WHERE t.commessa_id = $c ORDER BY l.id",
            r => new PlanLink(r.L("id"), r.L("predecessor_id"), r.L("successor_id"), r.IN("lag_days") ?? 0), ("$c", commessaId));

    /// <summary>Task → modifiche del registro collegate.</summary>
    public Dictionary<long, List<long>> ChangeLinks(long commessaId) =>
        _db.Query(
                "SELECT tc.task_id, tc.change_id FROM task_change tc JOIN task t ON t.id = tc.task_id WHERE t.commessa_id = $c",
                r => (Task: r.L("task_id"), Change: r.L("change_id")), ("$c", commessaId))
            .GroupBy(x => x.Task).ToDictionary(g => g.Key, g => g.Select(x => x.Change).ToList());

    public List<string> Assignees(long commessaId) =>
        _db.Query("SELECT DISTINCT assignee FROM task WHERE commessa_id = $c AND assignee IS NOT NULL AND assignee <> '' ORDER BY assignee",
            r => r.S("assignee"), ("$c", commessaId));

    public List<PlanEvent> Events(long commessaId, long? taskId = null, int limit = 1000) =>
        taskId == null
            ? _db.Query("SELECT * FROM task_event WHERE commessa_id = $c ORDER BY utc DESC, id DESC LIMIT $n", MapEvent,
                ("$c", commessaId), ("$n", limit))
            : _db.Query("SELECT * FROM task_event WHERE commessa_id = $c AND task_id = $t ORDER BY utc DESC, id DESC LIMIT $n", MapEvent,
                ("$c", commessaId), ("$t", taskId), ("$n", limit));

    /// <summary>L'ultima azione annullabile della commessa (date o campi, non ancora annullata).</summary>
    public string? LastUndoableBatch(long commessaId) =>
        _db.Scalar(
            @"SELECT batch FROM task_event WHERE commessa_id = $c AND batch IS NOT NULL AND undone_by IS NULL
                AND kind IN ('date', 'modificato', 'creato', 'collegato', 'scollegato')
              ORDER BY utc DESC, id DESC LIMIT 1", ("$c", commessaId)) as string;

    // ---------- scrittura ----------

    public long Insert(PlanTask t, string? user)
    {
        DateTime now = DateTime.UtcNow;
        using Db.Tx tx = _db.Begin();
        if (t.Sort == 0)
        {
            t.Sort = Convert.ToInt32(_db.Scalar("SELECT COALESCE(MAX(sort), 0) + 10 FROM task WHERE commessa_id = $c", ("$c", t.CommessaId)),
                CultureInfo.InvariantCulture);
        }

        t.CreatedUtc = t.UpdatedUtc = now;
        t.Id = _db.Insert(
            @"INSERT INTO task (commessa_id, parent_id, sort, kind, title, description, status, priority, assignee, start_date, end_date,
                locked, progress, estimated_hours, actual_hours, version_id, created_utc, updated_utc)
              VALUES ($c, $p, $s, $k, $t, $d, $st, $pr, $a, $sd, $ed, $l, $pg, $eh, $ah, $v, $now, $now)",
            Args(t, ("$c", t.CommessaId), ("$s", t.Sort), ("$now", now)));
        AddEvent(t, NewBatch(), now, user, PlanEventKinds.Created, PlanFields.Dates, null, PlanFields.Range(t.Start, t.End), null, false);
        tx.Commit();
        return t.Id;
    }

    /// <summary>Salva il task e scrive nello storico i campi cambiati (un lotto). Null se non e' cambiato nulla.</summary>
    public string? Update(PlanTask t, string? user, string? reason = null)
    {
        PlanTask old = Get(t.Id) ?? throw new InvalidOperationException("Task " + t.Id + " non trovato");
        Dictionary<string, string?> before = Values(old), after = Values(t);
        List<string> changed = after.Keys.Where(k => before[k] != after[k]).ToList();
        if (changed.Count == 0)
        {
            return null;
        }

        DateTime now = DateTime.UtcNow;
        string batch = NewBatch();
        using Db.Tx tx = _db.Begin();
        _db.Exec(
            @"UPDATE task SET parent_id = $p, kind = $k, title = $t, description = $d, status = $st, priority = $pr, assignee = $a,
                start_date = $sd, end_date = $ed, locked = $l, progress = $pg, estimated_hours = $eh, actual_hours = $ah,
                version_id = $v, updated_utc = $now
              WHERE id = $id",
            Args(t, ("$id", t.Id), ("$now", now)));
        foreach (string field in changed)
        {
            AddEvent(t, batch, now, user, field == PlanFields.Dates ? PlanEventKinds.Dates : PlanEventKinds.Modified, field, before[field], after[field],
                reason, false);
        }

        t.UpdatedUtc = now;
        tx.Commit();
        return batch;
    }

    /// <summary>Nuove date (spostamento a mano e cascata) in un solo lotto, con il motivo. Restituisce il lotto.</summary>
    public string ApplyDateChanges(long commessaId, IReadOnlyList<DateChange> changes, string? reason, string? user)
    {
        DateTime now = DateTime.UtcNow;
        string batch = NewBatch();
        using Db.Tx tx = _db.Begin();
        foreach (DateChange c in changes)
        {
            _db.Exec("UPDATE task SET start_date = $s, end_date = $e, updated_utc = $now WHERE id = $id AND commessa_id = $c",
                ("$s", c.NewStart), ("$e", c.NewEnd), ("$now", now), ("$id", c.TaskId), ("$c", commessaId));
            InsertEvent(commessaId, c.TaskId, c.Title, batch, now, user, PlanEventKinds.Dates, PlanFields.Dates,
                PlanFields.Range(c.OldStart, c.OldEnd), PlanFields.Range(c.NewStart, c.NewEnd), reason, c.Cascade);
        }

        tx.Commit();
        return batch;
    }

    /// <summary>Elimina il task (le fasi lasciano i figli al livello sopra); lo storico conserva il titolo.</summary>
    public void Delete(long taskId, string? user)
    {
        PlanTask? t = Get(taskId);
        if (t == null)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        using Db.Tx tx = _db.Begin();
        _db.Exec("UPDATE task SET parent_id = $p WHERE parent_id = $id", ("$p", t.ParentId), ("$id", taskId));
        AddEvent(t, NewBatch(), now, user, PlanEventKinds.Deleted, null, PlanStates.Italian(t.Kind), null, null, false);
        _db.Exec("DELETE FROM task WHERE id = $id", ("$id", taskId));
        tx.Commit();
    }

    /// <summary>Ordine e fasi dopo indenta/rientra/sposta; il cambio di fase va nello storico.</summary>
    public void Reorder(long commessaId, IReadOnlyList<(long Id, long? ParentId, int Sort)> rows, string? user)
    {
        Dictionary<long, PlanTask> current = Tasks(commessaId).ToDictionary(t => t.Id);
        DateTime now = DateTime.UtcNow;
        string batch = NewBatch();
        using Db.Tx tx = _db.Begin();
        foreach ((long id, long? parent, int sort) in rows)
        {
            if (!current.TryGetValue(id, out PlanTask? t) || (t.ParentId == parent && t.Sort == sort))
            {
                continue;
            }

            _db.Exec("UPDATE task SET parent_id = $p, sort = $s WHERE id = $id", ("$p", parent), ("$s", sort), ("$id", id));
            if (t.ParentId != parent)
            {
                AddEvent(t, batch, now, user, PlanEventKinds.Modified, "parent_id", Str(t.ParentId), Str(parent), null, false);
            }
        }

        tx.Commit();
    }

    public long AddLink(long predecessorId, long successorId, int lagDays, string? user)
    {
        PlanTask succ = Get(successorId) ?? throw new InvalidOperationException("Task " + successorId + " non trovato");
        PlanTask pred = Get(predecessorId) ?? throw new InvalidOperationException("Task " + predecessorId + " non trovato");
        using Db.Tx tx = _db.Begin();
        long id = _db.Insert("INSERT INTO task_link (predecessor_id, successor_id, lag_days) VALUES ($p, $s, $l)",
            ("$p", predecessorId), ("$s", successorId), ("$l", lagDays));
        AddEvent(succ, NewBatch(), DateTime.UtcNow, user, PlanEventKinds.Linked, "predecessor", null, LinkText(pred, lagDays, id), null, false);
        tx.Commit();
        return id;
    }

    public void RemoveLink(long linkId, string? user)
    {
        PlanLink? link = _db.Query("SELECT * FROM task_link WHERE id = $id",
            r => new PlanLink(r.L("id"), r.L("predecessor_id"), r.L("successor_id"), r.IN("lag_days") ?? 0), ("$id", linkId)).FirstOrDefault();
        if (link == null)
        {
            return;
        }

        PlanTask? succ = Get(link.SuccessorId), pred = Get(link.PredecessorId);
        using Db.Tx tx = _db.Begin();
        _db.Exec("DELETE FROM task_link WHERE id = $id", ("$id", linkId));
        if (succ != null && pred != null)
        {
            AddEvent(succ, NewBatch(), DateTime.UtcNow, user, PlanEventKinds.Unlinked, "predecessor", LinkText(pred, link.LagDays, linkId), null, null, false);
        }

        tx.Commit();
    }

    public void SetLinkLag(long linkId, int lagDays)
    {
        _db.Exec("UPDATE task_link SET lag_days = $l WHERE id = $id", ("$l", lagDays), ("$id", linkId));
    }

    public void SetChanges(long taskId, IReadOnlyCollection<long> changeIds)
    {
        using Db.Tx tx = _db.Begin();
        _db.Exec("DELETE FROM task_change WHERE task_id = $t", ("$t", taskId));
        foreach (long c in changeIds.Distinct())
        {
            _db.Exec("INSERT INTO task_change (task_id, change_id) VALUES ($t, $c)", ("$t", taskId), ("$c", c));
        }

        tx.Commit();
    }

    // ---------- annullamento ----------

    /// <summary>
    /// Riporta indietro un'azione: solo se i task hanno ancora i valori scritti da quel
    /// lotto (se qualcuno li ha cambiati dopo, si rifiuta e si dice quale).
    /// </summary>
    public PlanUndoResult UndoBatch(string batch, string? user)
    {
        List<PlanEvent> events = _db.Query("SELECT * FROM task_event WHERE batch = $b AND undone_by IS NULL ORDER BY id DESC", MapEvent, ("$b", batch));
        if (events.Count == 0)
        {
            return new PlanUndoResult(false, "Niente da annullare.");
        }

        if (events.Any(e => e.Kind == PlanEventKinds.Deleted))
        {
            return new PlanUndoResult(false, "Un'eliminazione non si annulla da qui.");
        }

        foreach (PlanEvent e in events.Where(e => e.Kind is PlanEventKinds.Dates or PlanEventKinds.Modified))
        {
            PlanTask? t = e.TaskId == null ? null : Get(e.TaskId.Value);
            if (t == null)
            {
                return new PlanUndoResult(false, "\"" + e.TaskTitle + "\" non esiste piu'.");
            }

            if (!Columns.Contains(e.Field ?? "") && e.Field != PlanFields.Dates)
            {
                return new PlanUndoResult(false, "Campo non annullabile: " + e.Field);
            }

            if (Values(t)[e.Field!] != e.NewValue)
            {
                return new PlanUndoResult(false, "\"" + t.Title + "\" e' stato cambiato di nuovo dopo: annullamento rifiutato.");
            }
        }

        // Togliere un task appena creato si puo' solo se dopo non e' stato toccato.
        foreach (PlanEvent e in events.Where(e => e.Kind == PlanEventKinds.Created))
        {
            long later = Convert.ToInt64(_db.Scalar(
                "SELECT COUNT(*) FROM task_event WHERE task_id = $t AND id > $id AND undone_by IS NULL AND batch <> $b",
                ("$t", e.TaskId), ("$id", e.Id), ("$b", batch)), CultureInfo.InvariantCulture);
            if (later > 0)
            {
                return new PlanUndoResult(false, "\"" + e.TaskTitle + "\" e' stato cambiato dopo la creazione: annullamento rifiutato.");
            }
        }

        string undo = NewBatch();
        DateTime now = DateTime.UtcNow;
        using Db.Tx tx = _db.Begin();
        foreach (PlanEvent e in events)
        {
            switch (e.Kind)
            {
                case PlanEventKinds.Dates:
                    PlanFields.TryParseRange(e.OldValue, out DateOnly s, out DateOnly en);
                    _db.Exec("UPDATE task SET start_date = $s, end_date = $e, updated_utc = $now WHERE id = $id",
                        ("$s", s), ("$e", en), ("$now", now), ("$id", e.TaskId));
                    break;
                case PlanEventKinds.Modified:
                    _db.Exec("UPDATE task SET " + e.Field + " = $v, updated_utc = $now WHERE id = $id",
                        ("$v", (object?)e.OldValue), ("$now", now), ("$id", e.TaskId));
                    break;
                case PlanEventKinds.Created:
                    _db.Exec("DELETE FROM task WHERE id = $id", ("$id", e.TaskId));
                    break;
                case PlanEventKinds.Linked when LinkIdOf(e.NewValue) is long lid:
                    _db.Exec("DELETE FROM task_link WHERE id = $id", ("$id", lid));
                    break;
                case PlanEventKinds.Unlinked when LinkIdOf(e.OldValue) is long:
                    // il collegamento tolto non si ricostruisce da qui: resta nello storico
                    break;
            }

            InsertEvent(e.CommessaId, e.TaskId, e.TaskTitle, undo, now, user, PlanEventKinds.Undone, e.Field, e.NewValue, e.OldValue,
                "annullato: " + PlanFields.Label(e.Field).ToLowerInvariant(), e.Cascade);
        }

        _db.Exec("UPDATE task_event SET undone_by = $u WHERE batch = $b AND undone_by IS NULL", ("$u", undo), ("$b", batch));
        tx.Commit();
        return new PlanUndoResult(true, events.Count == 1 ? "Annullato: " + events[0].TaskTitle : events.Count + " cambiamenti annullati", undo);
    }

    // ---------- interni ----------

    /// <summary>I valori dei campi come testo (per lo storico e per i confronti).</summary>
    internal static Dictionary<string, string?> Values(PlanTask t) => new(StringComparer.Ordinal)
    {
        ["title"] = t.Title,
        ["description"] = string.IsNullOrWhiteSpace(t.Description) ? null : t.Description,
        ["status"] = PlanStates.ToDb(t.Status),
        ["priority"] = PlanStates.ToDb(t.Priority),
        ["assignee"] = string.IsNullOrWhiteSpace(t.Assignee) ? null : t.Assignee,
        ["kind"] = PlanStates.ToDb(t.Kind),
        ["parent_id"] = Str(t.ParentId),
        ["locked"] = t.Locked ? "1" : "0",
        ["progress"] = t.Progress.ToString(CultureInfo.InvariantCulture),
        ["estimated_hours"] = t.EstimatedHours?.ToString("0.##", CultureInfo.InvariantCulture),
        ["actual_hours"] = t.ActualHours?.ToString("0.##", CultureInfo.InvariantCulture),
        ["version_id"] = Str(t.VersionId),
        [PlanFields.Dates] = PlanFields.Range(t.Start, t.End),
    };

    private static string? Str(long? v) => v?.ToString(CultureInfo.InvariantCulture);

    /// <summary>"Montaggio quadri (+2 gg) #15": il numero e' l'id del collegamento.</summary>
    private static string LinkText(PlanTask pred, int lag, long linkId) =>
        pred.Title + (lag != 0 ? " (" + (lag > 0 ? "+" : "") + lag.ToString(CultureInfo.InvariantCulture) + " gg)" : "") + " #" +
        linkId.ToString(CultureInfo.InvariantCulture);

    private static long? LinkIdOf(string? text)
    {
        int i = text?.LastIndexOf(" #", StringComparison.Ordinal) ?? -1;
        return i >= 0 && long.TryParse(text![(i + 2)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) ? id : null;
    }

    private static (string Name, object? Value)[] Args(PlanTask t, params (string Name, object? Value)[] extra) =>
        new (string Name, object? Value)[]
        {
            ("$p", t.ParentId), ("$k", PlanStates.ToDb(t.Kind)), ("$t", t.Title.Trim()), ("$d", string.IsNullOrWhiteSpace(t.Description) ? null : t.Description),
            ("$st", PlanStates.ToDb(t.Status)), ("$pr", PlanStates.ToDb(t.Priority)), ("$a", string.IsNullOrWhiteSpace(t.Assignee) ? null : t.Assignee.Trim()),
            ("$sd", t.Start), ("$ed", t.IsMilestone ? t.Start : t.End), ("$l", t.Locked), ("$pg", Math.Clamp(t.Progress, 0, 100)),
            ("$eh", t.EstimatedHours), ("$ah", t.ActualHours), ("$v", t.VersionId),
        }.Concat(extra).ToArray();

    private void AddEvent(PlanTask t, string batch, DateTime utc, string? user, string kind, string? field, string? oldValue, string? newValue,
        string? reason, bool cascade) =>
        InsertEvent(t.CommessaId, t.Id, t.Title, batch, utc, user, kind, field, oldValue, newValue, reason, cascade);

    private void InsertEvent(long commessaId, long? taskId, string title, string batch, DateTime utc, string? user, string kind, string? field,
        string? oldValue, string? newValue, string? reason, bool cascade)
    {
        _db.Exec(
            @"INSERT INTO task_event (commessa_id, task_id, task_title, batch, utc, user_name, kind, field, old_value, new_value, reason, cascade)
              VALUES ($c, $t, $title, $b, $u, $user, $k, $f, $o, $n, $r, $cas)",
            ("$c", commessaId), ("$t", taskId), ("$title", title), ("$b", batch), ("$u", utc), ("$user", user), ("$k", kind), ("$f", field),
            ("$o", oldValue), ("$n", newValue), ("$r", string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()), ("$cas", cascade));
    }

    internal static PlanTask Map(SqliteDataReader r) => new()
    {
        Id = r.L("id"),
        CommessaId = r.L("commessa_id"),
        ParentId = r.LN("parent_id"),
        Sort = r.IN("sort") ?? 0,
        Kind = PlanStates.KindFromDb(r.Str("kind")),
        Title = r.S("title"),
        Description = r.Str("description"),
        Status = PlanStates.StatusFromDb(r.Str("status")),
        Priority = PlanStates.PriorityFromDb(r.Str("priority")),
        Assignee = r.Str("assignee"),
        Start = r.Date("start_date") ?? DateOnly.FromDateTime(DateTime.Today),
        End = r.Date("end_date") ?? DateOnly.FromDateTime(DateTime.Today),
        Locked = r.B("locked"),
        Progress = r.IN("progress") ?? 0,
        EstimatedHours = r.DN("estimated_hours"),
        ActualHours = r.DN("actual_hours"),
        VersionId = r.LN("version_id"),
        CreatedUtc = r.Utc("created_utc") ?? default,
        UpdatedUtc = r.Utc("updated_utc") ?? default,
    };

    private static PlanEvent MapEvent(SqliteDataReader r) => new()
    {
        Id = r.L("id"),
        CommessaId = r.L("commessa_id"),
        TaskId = r.LN("task_id"),
        TaskTitle = r.S("task_title"),
        Batch = r.Str("batch"),
        Utc = r.Utc("utc") ?? default,
        User = r.Str("user_name"),
        Kind = r.S("kind"),
        Field = r.Str("field"),
        OldValue = r.Str("old_value"),
        NewValue = r.Str("new_value"),
        Reason = r.Str("reason"),
        Cascade = r.B("cascade"),
        UndoneBy = r.Str("undone_by"),
    };
}
