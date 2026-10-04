using System.Globalization;
using Microsoft.Data.Sqlite;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Scanning;

namespace TiaTracker.Data;

public sealed record ScanSummary(int Found, int New, int Updated, int Missing);

public sealed class VersionRepository
{
    private readonly Db _db;

    public VersionRepository(Db db)
    {
        _db = db;
    }

    public List<ProjectVersion> ByCommessa(long commessaId) =>
        _db.Query("SELECT * FROM version WHERE commessa_id = $c ORDER BY sort_key DESC, project_name", Map, ("$c", commessaId));

    public ProjectVersion? Get(long id) =>
        _db.Query("SELECT * FROM version WHERE id = $id", Map, ("$id", id)).FirstOrDefault();

    /// <summary>Le versioni di TIA presenti nell'archivio (per sapere quali worker servono).</summary>
    public List<int> TiaMajors() =>
        _db.Query("SELECT DISTINCT tia_major FROM version WHERE tia_major IS NOT NULL ORDER BY tia_major", r => (int)r.L("tia_major"));

    /// <summary>
    /// Allinea il DB a una scansione. Idempotente: aggiorna i campi letti dal
    /// disco, non tocca i flag manuali, non cancella mai una versione (quelle
    /// sparite diventano "missing").
    /// </summary>
    public ScanSummary ApplyScan(long commessaId, ScanRoot root, IReadOnlyList<ScannedVersion> scanned)
    {
        DateTime now = DateTime.UtcNow;
        Dictionary<string, ProjectVersion> existing = ByCommessa(commessaId)
            .ToDictionary(v => v.ProjectName, StringComparer.OrdinalIgnoreCase);
        int created = 0, updated = 0, missing = 0;

        using Db.Tx tx = _db.Begin();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (ScannedVersion s in scanned)
        {
            seen.Add(s.ProjectName);
            DateTime? lastBackup = s.BackupZips.Count > 0 ? s.BackupZips[0].WrittenUtc : null;
            (string Name, object? Value)[] args =
            {
                ("$c", commessaId), ("$root", root.Id), ("$label", s.Parsed.Label), ("$sort", s.SortKey),
                ("$name", s.ProjectName), ("$tia", s.TiaMajor), ("$tiat", s.TiaVersionText),
                ("$folder", s.FolderRel), ("$pf", s.ProjectFileRel), ("$rar", s.RarRel), ("$bak", s.BackupRel),
                ("$hf", s.HasFolder), ("$hr", s.HasRar), ("$hb", s.HasBackup), ("$bc", s.BackupZips.Count),
                ("$lb", lastBackup), ("$ls", s.LastSavedUtc), ("$now", now),
            };

            if (!existing.TryGetValue(s.ProjectName, out ProjectVersion? old))
            {
                long id = _db.Insert(
                    @"INSERT INTO version (commessa_id, scan_root_id, label, sort_key, project_name, tia_major, tia_version_text,
                        folder_rel, project_file_rel, rar_rel, backup_rel, has_folder, has_rar, has_backup, backup_count,
                        last_backup_utc, last_saved_utc, first_seen_utc, last_seen_utc, missing)
                      VALUES ($c, $root, $label, $sort, $name, $tia, $tiat, $folder, $pf, $rar, $bak, $hf, $hr, $hb, $bc,
                        $lb, $ls, $now, $now, 0)", args);
                AddEvent(id, "trovata", Where(s), now);
                created++;
                continue;
            }

            bool changed = old.Label != s.Parsed.Label || old.SortKey != s.SortKey || old.HasFolder != s.HasFolder ||
                           old.HasRar != s.HasRar || old.HasBackup != s.HasBackup || old.BackupCount != s.BackupZips.Count ||
                           old.LastSavedUtc != Trunc(s.LastSavedUtc) || old.Missing || old.TiaVersionText != s.TiaVersionText;

            _db.Exec(
                @"UPDATE version SET scan_root_id = $root, label = $label, sort_key = $sort, tia_major = $tia, tia_version_text = $tiat,
                    folder_rel = $folder, project_file_rel = $pf, rar_rel = $rar, backup_rel = $bak,
                    has_folder = $hf, has_rar = $hr, has_backup = $hb, backup_count = $bc,
                    last_backup_utc = $lb, last_saved_utc = $ls, last_seen_utc = $now, missing = 0
                  WHERE commessa_id = $c AND project_name = $name", args);

            if (old.LastSavedUtc != null && s.LastSavedUtc != null && Trunc(s.LastSavedUtc) != old.LastSavedUtc)
            {
                AddEvent(old.Id, "salvataggio", "Progetto salvato in TIA il " + Local(s.LastSavedUtc.Value), now);
            }

            if (old.BackupCount != s.BackupZips.Count && s.BackupZips.Count > old.BackupCount)
            {
                AddEvent(old.Id, "backup", (s.BackupZips.Count - old.BackupCount) + " nuovi backup TIA", now);
            }

            if (old.Missing)
            {
                AddEvent(old.Id, "ritrovata", Where(s), now);
            }

            if (changed)
            {
                updated++;
            }
        }

        foreach (ProjectVersion old in existing.Values)
        {
            if (seen.Contains(old.ProjectName) || old.Missing || old.ScanRootId != root.Id)
            {
                continue;
            }

            // I percorsi restano per la storia; i flag no, altrimenti l'app propone di aprire cartelle che non ci sono.
            _db.Exec("UPDATE version SET missing = 1, has_folder = 0, has_rar = 0, has_backup = 0 WHERE id = $id", ("$id", old.Id));
            AddEvent(old.Id, "sparita", "Non piu' trovata nella cartella di scansione", now);
            missing++;
        }

        root.LastScanUtc = now;
        _db.Exec("UPDATE scan_root SET last_scan_utc = $ls WHERE id = $id", ("$ls", now), ("$id", root.Id));
        tx.Commit();
        return new ScanSummary(scanned.Count, created, updated, missing);
    }

    // ---------- stato e carichi sui PLC ----------

    /// <summary>Esito di un carico: le versioni tolte da un PLC e se sono diventate Vecchie.</summary>
    public sealed record Replaced(long VersionId, string Label, string Plc, bool BecameOld);

    public List<VersionLoad> Loads(long commessaId) =>
        _db.Query("SELECT * FROM version_load WHERE commessa_id = $c ORDER BY loaded_utc DESC, id DESC", MapLoad, ("$c", commessaId));

    public List<VersionLoad> CurrentLoads(long commessaId) =>
        _db.Query("SELECT * FROM version_load WHERE commessa_id = $c AND ended_utc IS NULL ORDER BY plc", MapLoad, ("$c", commessaId));

    public List<VersionLoad> LoadsOfVersion(long versionId) =>
        _db.Query("SELECT * FROM version_load WHERE version_id = $v ORDER BY loaded_utc DESC, id DESC", MapLoad, ("$v", versionId));

    /// <summary>I PLC gia' usati nei carichi della commessa (per proporli nel dialogo).</summary>
    public List<string> KnownPlcs(long commessaId) =>
        _db.Query("SELECT DISTINCT plc FROM version_load WHERE commessa_id = $c AND plc <> '' ORDER BY plc", r => r.S("plc"), ("$c", commessaId));

    /// <summary>
    /// Cambia lo stato (non Caricata: per quella c'e' RecordLoad, che vuole PLC e data).
    /// I carichi correnti restano: chi toglie una versione dal PLC usa EndLoads.
    /// </summary>
    public void SetState(long versionId, VersionState state, string? note = null)
    {
        if (state == VersionState.Caricata)
        {
            throw new ArgumentException("Per Caricata usare RecordLoad (PLC e data).", nameof(state));
        }

        ProjectVersion v = Get(versionId) ?? throw new InvalidOperationException("Versione inesistente");
        DateTime now = DateTime.UtcNow;
        using Db.Tx tx = _db.Begin();
        WriteState(v, state, Blank(note), now, null);
        SyncLegacyFlags(versionId);
        tx.Commit();
    }

    /// <summary>
    /// Registra la versione come caricata sui PLC indicati ("" = PLC senza nome). Sullo
    /// stesso PLC c'e' un solo carico corrente: quello precedente si chiude e, se la sua
    /// versione non e' piu' su nessun PLC ed era Caricata (o senza stato), diventa Vecchia.
    /// </summary>
    public List<Replaced> RecordLoad(long versionId, IReadOnlyList<string> plcs, DateTime loadedUtc, string? note)
    {
        ProjectVersion v = Get(versionId) ?? throw new InvalidOperationException("Versione inesistente");
        List<string> targets = plcs.Select(p => (p ?? "").Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (targets.Count == 0)
        {
            targets.Add("");
        }

        DateTime now = DateTime.UtcNow;
        List<Replaced> replaced = new();
        using Db.Tx tx = _db.Begin();

        // 1. Chiude i carichi correnti su quei PLC (anche quelli della stessa versione: ricaricata).
        Dictionary<long, List<string>> endedByVersion = new();
        foreach (string plc in targets)
        {
            foreach (VersionLoad cur in _db.Query(
                         "SELECT * FROM version_load WHERE commessa_id = $c AND plc = $p AND ended_utc IS NULL", MapLoad,
                         ("$c", v.CommessaId), ("$p", plc)))
            {
                bool same = cur.VersionId == versionId;
                _db.Exec("UPDATE version_load SET ended_utc = $u, ended_by_version_id = $by, ended_reason = $r WHERE id = $id",
                    ("$u", now), ("$by", versionId), ("$r", same ? "ricaricata" : "sostituita da " + v.Label), ("$id", cur.Id));
                if (!same)
                {
                    if (!endedByVersion.TryGetValue(cur.VersionId, out List<string>? list))
                    {
                        endedByVersion[cur.VersionId] = list = new List<string>();
                    }

                    list.Add(cur.PlcDisplay);
                }
            }
        }

        // 2. Le versioni sostituite: flag riallineati PRIMA di quelli della nuova (indice unico storico su loaded_plc).
        foreach ((long oldId, List<string> oldPlcs) in endedByVersion)
        {
            ProjectVersion old = Get(oldId)!;
            bool stillLoaded = CurrentLoadCount(oldId) > 0;
            bool becameOld = !stillLoaded && old.State is VersionState.Caricata or VersionState.None;
            string where = string.Join(", ", oldPlcs);
            AddEvent(oldId, "scaricata", "Sostituita su " + where + " da " + v.Label, now);
            if (becameOld)
            {
                WriteState(old, VersionState.Vecchia, null, now, "sostituita da " + v.Label);
            }

            SyncLegacyFlags(oldId);
            replaced.Add(new Replaced(oldId, old.Label, where, becameOld));
        }

        // 3. I nuovi carichi e lo stato Caricata.
        foreach (string plc in targets)
        {
            _db.Exec(
                @"INSERT INTO version_load (commessa_id, version_id, plc, loaded_utc, note, created_utc)
                  VALUES ($c, $v, $p, $u, $n, $now)",
                ("$c", v.CommessaId), ("$v", versionId), ("$p", plc), ("$u", loadedUtc), ("$n", Blank(note)), ("$now", now));
        }

        string plcText = string.Join(", ", targets.Select(p => p.Length == 0 ? "PLC" : p));
        AddEvent(versionId, "caricata", "Caricata su " + plcText + " il " + Local(loadedUtc) +
                                        (string.IsNullOrWhiteSpace(note) ? "" : " - " + note.Trim()), now);
        if (v.State != VersionState.Caricata)
        {
            WriteState(v, VersionState.Caricata, null, loadedUtc, null);
        }

        SyncLegacyFlags(versionId);
        tx.Commit();
        return replaced;
    }

    /// <summary>Toglie la versione dai PLC indicati (null = da tutti): non e' piu' sul PLC.</summary>
    public void EndLoads(long versionId, IReadOnlyList<string>? plcs, string reason)
    {
        DateTime now = DateTime.UtcNow;
        using Db.Tx tx = _db.Begin();
        List<VersionLoad> current = LoadsOfVersion(versionId).Where(l => l.IsCurrent).ToList();
        List<string> ended = new();
        foreach (VersionLoad l in current)
        {
            if (plcs != null && !plcs.Contains(l.Plc, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            _db.Exec("UPDATE version_load SET ended_utc = $u, ended_reason = $r WHERE id = $id",
                ("$u", now), ("$r", reason), ("$id", l.Id));
            ended.Add(l.PlcDisplay);
        }

        if (ended.Count > 0)
        {
            AddEvent(versionId, "scaricata", "Tolta da " + string.Join(", ", ended) + ": " + reason, now);
        }

        SyncLegacyFlags(versionId);
        tx.Commit();
    }

    /// <summary>Collega lo snapshot di prova ai carichi correnti della versione (tutti o di un PLC).</summary>
    public void AttachProofSnapshot(long versionId, long snapshotId, string? plc = null)
    {
        _db.Exec(
            @"UPDATE version_load SET proof_snapshot_id = $s
              WHERE version_id = $v AND ended_utc IS NULL AND ($p IS NULL OR plc = $p)",
            ("$s", snapshotId), ("$v", versionId), ("$p", plc));
    }

    // Compatibilita' con il vecchio modello a flag (e con i test che lo usano).

    public void SetLoaded(long versionId, DateTime loadedUtc, string? plc, string? note) =>
        RecordLoad(versionId, new[] { plc ?? "" }, loadedUtc, note);

    public void ClearLoaded(long versionId)
    {
        using Db.Tx tx = _db.Begin();
        EndLoads(versionId, null, "flag Caricata tolto a mano");
        if (Get(versionId)?.State == VersionState.Caricata)
        {
            SetState(versionId, VersionState.None);
        }

        tx.Commit();
    }

    public void SetActive(long versionId, bool active)
    {
        ProjectVersion? v = Get(versionId);
        if (active)
        {
            SetState(versionId, VersionState.InLavoro);
        }
        else if (v?.State == VersionState.InLavoro)
        {
            SetState(versionId, VersionState.None);
        }
    }

    public void SetArchived(long versionId, bool archived)
    {
        ProjectVersion? v = Get(versionId);
        if (archived)
        {
            SetState(versionId, VersionState.Archiviata);
        }
        else if (v?.State == VersionState.Archiviata)
        {
            SetState(versionId, VersionState.None);
        }
    }

    private int CurrentLoadCount(long versionId) =>
        Convert.ToInt32(_db.Scalar("SELECT COUNT(*) FROM version_load WHERE version_id = $v AND ended_utc IS NULL", ("$v", versionId)),
            CultureInfo.InvariantCulture);

    private void WriteState(ProjectVersion v, VersionState state, string? note, DateTime utc, string? why)
    {
        if (v.State == state && v.StateNote == note)
        {
            return;
        }

        _db.Exec("UPDATE version SET state = $s, state_utc = $u, state_note = $n WHERE id = $id",
            ("$s", VersionStates.ToDb(state)), ("$u", utc), ("$n", note), ("$id", v.Id));
        string from = v.State == VersionState.None ? "nessuno" : VersionStates.Italian(v.State);
        string to = state == VersionState.None ? "nessuno" : VersionStates.Italian(state);
        AddEvent(v.Id, "stato", from + " → " + to + (note != null ? " (" + note + ")" : "") + (why != null ? ": " + why : ""), DateTime.UtcNow);
        v.State = state;
        v.StateNote = note;
    }

    /// <summary>
    /// Flag storici da stato e carichi: is_loaded = ha un carico corrente (loaded_* = il piu'
    /// recente), is_active = In lavoro, is_archived = Archiviata. Mai NULL nei flag NOT NULL.
    /// </summary>
    private void SyncLegacyFlags(long versionId)
    {
        _db.Exec(
            @"UPDATE version SET
                is_loaded = CASE WHEN EXISTS (SELECT 1 FROM version_load l WHERE l.version_id = version.id AND l.ended_utc IS NULL) THEN 1 ELSE 0 END,
                loaded_plc = COALESCE((SELECT NULLIF(l.plc, '') FROM version_load l WHERE l.version_id = version.id AND l.ended_utc IS NULL
                                       ORDER BY l.loaded_utc DESC, l.id DESC LIMIT 1),
                                      CASE WHEN EXISTS (SELECT 1 FROM version_load l WHERE l.version_id = version.id AND l.ended_utc IS NULL)
                                           THEN NULL ELSE loaded_plc END),
                loaded_utc = COALESCE((SELECT l.loaded_utc FROM version_load l WHERE l.version_id = version.id AND l.ended_utc IS NULL
                                       ORDER BY l.loaded_utc DESC, l.id DESC LIMIT 1), loaded_utc),
                loaded_note = COALESCE((SELECT l.note FROM version_load l WHERE l.version_id = version.id AND l.ended_utc IS NULL
                                        ORDER BY l.loaded_utc DESC, l.id DESC LIMIT 1), loaded_note),
                is_active = CASE WHEN state = 'in_lavoro' THEN 1 ELSE 0 END,
                is_archived = CASE WHEN state = 'archiviata' THEN 1 ELSE 0 END
              WHERE id = $id",
            ("$id", versionId));
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    internal static VersionLoad MapLoad(SqliteDataReader r) => new()
    {
        Id = r.L("id"),
        CommessaId = r.L("commessa_id"),
        VersionId = r.L("version_id"),
        Plc = r.S("plc"),
        LoadedUtc = r.Utc("loaded_utc") ?? default,
        Note = r.Str("note"),
        ProofSnapshotId = r.LN("proof_snapshot_id"),
        CreatedUtc = r.Utc("created_utc") ?? default,
        EndedUtc = r.Utc("ended_utc"),
        EndedByVersionId = r.LN("ended_by_version_id"),
        EndedReason = r.Str("ended_reason"),
    };

    public void SetNote(long versionId, string? note)
    {
        _db.Exec("UPDATE version SET note = $n WHERE id = $id", ("$n", string.IsNullOrWhiteSpace(note) ? null : note), ("$id", versionId));
    }

    public List<VersionEvent> Events(long versionId) =>
        _db.Query("SELECT * FROM version_event WHERE version_id = $v ORDER BY utc DESC, id DESC", r => new VersionEvent
        {
            Id = r.L("id"),
            VersionId = r.L("version_id"),
            Utc = r.Utc("utc") ?? default,
            Kind = r.S("kind"),
            Detail = r.Str("detail"),
        }, ("$v", versionId));

    public void AddEvent(long versionId, string kind, string? detail, DateTime? utc = null)
    {
        _db.Exec("INSERT INTO version_event (version_id, utc, kind, detail) VALUES ($v, $u, $k, $d)",
            ("$v", versionId), ("$u", utc ?? DateTime.UtcNow), ("$k", kind), ("$d", detail));
    }

    private static string Where(ScannedVersion s)
    {
        List<string> parts = new();
        if (s.HasFolder)
        {
            parts.Add("cartella");
        }

        if (s.HasRar)
        {
            parts.Add("rar");
        }

        if (s.HasBackup)
        {
            parts.Add(s.BackupZips.Count + " backup");
        }

        return string.Join(", ", parts);
    }

    /// <summary>Il DB tiene i millisecondi: si confronta alla stessa risoluzione.</summary>
    private static DateTime? Trunc(DateTime? v) =>
        v == null ? null : new DateTime(v.Value.Ticks - v.Value.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

    private static string Local(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    internal static ProjectVersion Map(SqliteDataReader r) => new()
    {
        Id = r.L("id"),
        CommessaId = r.L("commessa_id"),
        ScanRootId = r.LN("scan_root_id"),
        Label = r.S("label"),
        SortKey = r.S("sort_key"),
        ProjectName = r.S("project_name"),
        TiaMajor = r.IN("tia_major"),
        TiaVersionText = r.Str("tia_version_text"),
        FolderRel = r.Str("folder_rel"),
        ProjectFileRel = r.Str("project_file_rel"),
        RarRel = r.Str("rar_rel"),
        BackupRel = r.Str("backup_rel"),
        HasFolder = r.B("has_folder"),
        HasRar = r.B("has_rar"),
        HasBackup = r.B("has_backup"),
        BackupCount = r.IN("backup_count") ?? 0,
        LastBackupUtc = r.Utc("last_backup_utc"),
        LastSavedUtc = r.Utc("last_saved_utc"),
        State = VersionStates.FromDb(r.Str("state")),
        StateUtc = r.Utc("state_utc"),
        StateNote = r.Str("state_note"),
        IsLoaded = r.B("is_loaded"),
        LoadedUtc = r.Utc("loaded_utc"),
        LoadedPlc = r.Str("loaded_plc"),
        LoadedNote = r.Str("loaded_note"),
        IsActive = r.B("is_active"),
        IsArchived = r.B("is_archived"),
        Note = r.Str("note"),
        FirstSeenUtc = r.Utc("first_seen_utc") ?? default,
        LastSeenUtc = r.Utc("last_seen_utc"),
        Missing = r.B("missing"),
    };
}
