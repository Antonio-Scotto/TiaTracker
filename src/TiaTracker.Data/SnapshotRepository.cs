using Microsoft.Data.Sqlite;
using TiaTracker.Core.Domain;

namespace TiaTracker.Data;

public sealed class SnapshotRepository
{
    private readonly Db _db;

    public SnapshotRepository(Db db)
    {
        _db = db;
    }

    /// <summary>Inserisce snapshot e righe in una transazione (il CAS va riempito prima).</summary>
    public long Insert(Snapshot s, IEnumerable<SnapshotItem> items)
    {
        using Db.Tx tx = _db.Begin();
        s.Id = _db.Insert(
            @"INSERT INTO snapshot (version_id, created_utc, source, source_detail, project_path, plc_name, project_modified,
                project_saved_utc, online_state, norm_version, worker_version, tia_major, item_count, failed_count, partial,
                manifest_hash, timings_json, note)
              VALUES ($v, $cr, $src, $sd, $pp, $plc, $pm, $ps, $os, $nv, $wv, $tia, $ic, $fc, $part, $mh, $tj, $note)",
            ("$v", s.VersionId), ("$cr", s.CreatedUtc), ("$src", s.Source), ("$sd", s.SourceDetail), ("$pp", s.ProjectPath),
            ("$plc", s.PlcName), ("$pm", s.ProjectModified), ("$ps", s.ProjectSavedUtc), ("$os", s.OnlineState),
            ("$nv", s.NormVersion), ("$wv", s.WorkerVersion), ("$tia", s.TiaMajor), ("$ic", s.ItemCount), ("$fc", s.FailedCount),
            ("$part", s.Partial), ("$mh", s.ManifestHash), ("$tj", s.TimingsJson), ("$note", s.Note));

        using (SqliteCommand cmd = _db.CreateCommand(
                   @"INSERT INTO snapshot_item (snapshot_id, family, kind, name, group_path, number, language, instance_of, state, reason,
                    attrs_json, code_modified_attr, interface_modified_attr, compiled_attr, h_all, h_code, h_iface, h_init, h_text,
                    h_meta, xml_ref, scl_ref, units_json, changed_during_export)
                  VALUES ($s, $f, $k, $n, $g, $num, $lang, $inst, $state, $reason, $attrs, $cm, $im, $comp, $ha, $hc, $hi, $hn, $ht,
                    $hm, $xml, $scl, $units, $cde)"))
        {
            string[] names =
            {
                "$s", "$f", "$k", "$n", "$g", "$num", "$lang", "$inst", "$state", "$reason", "$attrs", "$cm", "$im", "$comp",
                "$ha", "$hc", "$hi", "$hn", "$ht", "$hm", "$xml", "$scl", "$units", "$cde",
            };
            foreach (string n in names)
            {
                cmd.Parameters.Add(new SqliteParameter(n, null));
            }

            foreach (SnapshotItem i in items)
            {
                i.SnapshotId = s.Id;
                object?[] values =
                {
                    s.Id, i.Family, i.Kind, i.Name, i.GroupPath, i.Number, i.Language, i.InstanceOf, i.State, i.Reason,
                    i.AttrsJson, i.CodeModifiedAttr, i.InterfaceModifiedAttr, i.CompiledAttr, i.HAll, i.HCode, i.HIface, i.HInit,
                    i.HText, i.HMeta, i.XmlRef, i.SclRef, i.UnitsJson, i.ChangedDuringExport ? 1 : 0,
                };
                for (int k = 0; k < values.Length; k++)
                {
                    cmd.Parameters[k].Value = values[k] ?? DBNull.Value;
                }

                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
        return s.Id;
    }

    public List<Snapshot> ByVersion(long versionId) =>
        _db.Query("SELECT * FROM snapshot WHERE version_id = $v ORDER BY created_utc DESC", Map, ("$v", versionId));

    public Snapshot? Get(long id) =>
        _db.Query("SELECT * FROM snapshot WHERE id = $id", Map, ("$id", id)).FirstOrDefault();

    public Snapshot? Latest(long versionId) => ByVersion(versionId).FirstOrDefault();

    public Snapshot? LastAttach(long versionId) =>
        _db.Query("SELECT * FROM snapshot WHERE version_id = $v AND source = 'attach' ORDER BY created_utc DESC LIMIT 1",
            Map, ("$v", versionId)).FirstOrDefault();

    /// <summary>Ultimo snapshot attach per ogni versione della commessa (per il badge Non salvata).</summary>
    public Dictionary<long, Snapshot> LastAttachByVersion(long commessaId) =>
        _db.Query(
                @"SELECT s.* FROM snapshot s JOIN version v ON v.id = s.version_id
                  WHERE v.commessa_id = $c AND s.source = 'attach'
                    AND s.created_utc = (SELECT MAX(created_utc) FROM snapshot x WHERE x.version_id = s.version_id AND x.source = 'attach')",
                Map, ("$c", commessaId))
            .GroupBy(s => s.VersionId)
            .ToDictionary(g => g.Key, g => g.First());

    /// <summary>Ultimo snapshot (qualunque sorgente) per ogni versione della commessa (per "Snapshot da aggiornare").</summary>
    public Dictionary<long, Snapshot> LatestByVersion(long commessaId) =>
        _db.Query(
                @"SELECT s.* FROM snapshot s JOIN version v ON v.id = s.version_id
                  WHERE v.commessa_id = $c
                    AND s.created_utc = (SELECT MAX(created_utc) FROM snapshot x WHERE x.version_id = s.version_id)",
                Map, ("$c", commessaId))
            .GroupBy(s => s.VersionId)
            .ToDictionary(g => g.Key, g => g.First());

    public List<Snapshot> ByCommessa(long commessaId) =>
        _db.Query(
            "SELECT s.* FROM snapshot s JOIN version v ON v.id = s.version_id WHERE v.commessa_id = $c ORDER BY s.created_utc DESC",
            Map, ("$c", commessaId));

    public List<SnapshotItem> Items(long snapshotId) =>
        _db.Query("SELECT * FROM snapshot_item WHERE snapshot_id = $s ORDER BY family, group_path, name", MapItem, ("$s", snapshotId));

    public SnapshotItem? Item(long id) =>
        _db.Query("SELECT * FROM snapshot_item WHERE id = $id", MapItem, ("$id", id)).FirstOrDefault();

    /// <summary>Nomi di blocchi e UDT dell'ultimo snapshot di ogni versione: per l'autocompletamento.</summary>
    public List<string> KnownNames(long commessaId) =>
        _db.Query(
            @"SELECT DISTINCT i.name FROM snapshot_item i
              WHERE i.snapshot_id IN (SELECT (SELECT id FROM snapshot s WHERE s.version_id = v.id ORDER BY s.created_utc DESC LIMIT 1)
                                      FROM version v WHERE v.commessa_id = $c)
              ORDER BY i.name",
            r => r.S("name"), ("$c", commessaId));

    public void Delete(long snapshotId)
    {
        _db.Exec("DELETE FROM snapshot WHERE id = $id", ("$id", snapshotId));
    }

    public void SetNote(long snapshotId, string? note)
    {
        _db.Exec("UPDATE snapshot SET note = $n WHERE id = $id", ("$n", note), ("$id", snapshotId));
    }

    internal static Snapshot Map(SqliteDataReader r) => new()
    {
        Id = r.L("id"),
        VersionId = r.L("version_id"),
        CreatedUtc = r.Utc("created_utc") ?? default,
        Source = r.S("source"),
        SourceDetail = r.Str("source_detail"),
        ProjectPath = r.Str("project_path"),
        PlcName = r.Str("plc_name"),
        ProjectModified = r.BN("project_modified"),
        ProjectSavedUtc = r.Utc("project_saved_utc"),
        OnlineState = r.Str("online_state"),
        NormVersion = r.IN("norm_version") ?? 0,
        WorkerVersion = r.Str("worker_version"),
        TiaMajor = r.IN("tia_major"),
        ItemCount = r.IN("item_count") ?? 0,
        FailedCount = r.IN("failed_count") ?? 0,
        Partial = r.B("partial"),
        ManifestHash = r.Str("manifest_hash"),
        TimingsJson = r.Str("timings_json"),
        Note = r.Str("note"),
    };

    internal static SnapshotItem MapItem(SqliteDataReader r) => new()
    {
        Id = r.L("id"),
        SnapshotId = r.L("snapshot_id"),
        Family = r.S("family"),
        Kind = r.S("kind"),
        Name = r.S("name"),
        GroupPath = r.S("group_path"),
        Number = r.IN("number"),
        Language = r.Str("language"),
        InstanceOf = r.Str("instance_of"),
        State = r.S("state"),
        Reason = r.Str("reason"),
        AttrsJson = r.Str("attrs_json"),
        CodeModifiedAttr = r.Str("code_modified_attr"),
        InterfaceModifiedAttr = r.Str("interface_modified_attr"),
        CompiledAttr = r.Str("compiled_attr"),
        HAll = r.Str("h_all"),
        HCode = r.Str("h_code"),
        HIface = r.Str("h_iface"),
        HInit = r.Str("h_init"),
        HText = r.Str("h_text"),
        HMeta = r.Str("h_meta"),
        XmlRef = r.Str("xml_ref"),
        SclRef = r.Str("scl_ref"),
        UnitsJson = r.Str("units_json"),
        ChangedDuringExport = r.B("changed_during_export"),
    };
}
