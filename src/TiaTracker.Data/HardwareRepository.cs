using Microsoft.Data.Sqlite;
using TiaTracker.Core.Domain;

namespace TiaTracker.Data;

/// <summary>Gli export di hardware, rete e tag (i dati sono nel content store).</summary>
public sealed class HardwareRepository
{
    private readonly Db _db;

    public HardwareRepository(Db db)
    {
        _db = db;
    }

    public long Insert(HwSnapshot s)
    {
        s.Id = _db.Insert(
            @"INSERT INTO hw_snapshot (version_id, snapshot_id, created_utc, source, source_detail, project_path, project_modified,
                worker_version, tia_major, format, data_hash, device_count, module_count, ip_count, tag_count, io_tag_count,
                partial, warnings_json, timings_json)
              VALUES ($v, $s, $c, $src, $det, $pp, $pm, $wv, $tia, $f, $h, $dc, $mc, $ic, $tc, $io, $p, $w, $t)",
            ("$v", s.VersionId), ("$s", s.SnapshotId), ("$c", s.CreatedUtc), ("$src", s.Source), ("$det", s.SourceDetail),
            ("$pp", s.ProjectPath), ("$pm", s.ProjectModified), ("$wv", s.WorkerVersion), ("$tia", s.TiaMajor), ("$f", s.Format),
            ("$h", s.DataHash), ("$dc", s.DeviceCount), ("$mc", s.ModuleCount), ("$ic", s.IpCount), ("$tc", s.TagCount),
            ("$io", s.IoTagCount), ("$p", s.Partial), ("$w", s.WarningsJson), ("$t", s.TimingsJson));
        return s.Id;
    }

    public HwSnapshot? Get(long id) =>
        _db.Query("SELECT * FROM hw_snapshot WHERE id = $id", Map, ("$id", id)).FirstOrDefault();

    public List<HwSnapshot> ByVersion(long versionId) =>
        _db.Query("SELECT * FROM hw_snapshot WHERE version_id = $v ORDER BY created_utc DESC, id DESC", Map, ("$v", versionId));

    public List<HwSnapshot> ByCommessa(long commessaId) =>
        _db.Query(
            @"SELECT h.* FROM hw_snapshot h JOIN version v ON v.id = h.version_id
              WHERE v.commessa_id = $c ORDER BY h.created_utc DESC, h.id DESC",
            Map, ("$c", commessaId));

    public HwSnapshot? Latest(long versionId) => ByVersion(versionId).FirstOrDefault();

    /// <summary>L'ultimo export hardware di ogni versione della commessa.</summary>
    public Dictionary<long, HwSnapshot> LatestByVersion(long commessaId) =>
        ByCommessa(commessaId).GroupBy(h => h.VersionId).ToDictionary(g => g.Key, g => g.First());

    public void Delete(long id)
    {
        _db.Exec("DELETE FROM hw_snapshot WHERE id = $id", ("$id", id));
    }

    internal static HwSnapshot Map(SqliteDataReader r) => new()
    {
        Id = r.L("id"),
        VersionId = r.L("version_id"),
        SnapshotId = r.LN("snapshot_id"),
        CreatedUtc = r.Utc("created_utc") ?? default,
        Source = r.S("source"),
        SourceDetail = r.Str("source_detail"),
        ProjectPath = r.Str("project_path"),
        ProjectModified = r.BN("project_modified"),
        WorkerVersion = r.Str("worker_version"),
        TiaMajor = r.IN("tia_major"),
        Format = r.IN("format") ?? 1,
        DataHash = r.S("data_hash"),
        DeviceCount = r.IN("device_count") ?? 0,
        ModuleCount = r.IN("module_count") ?? 0,
        IpCount = r.IN("ip_count") ?? 0,
        TagCount = r.IN("tag_count") ?? 0,
        IoTagCount = r.IN("io_tag_count") ?? 0,
        Partial = r.B("partial"),
        WarningsJson = r.Str("warnings_json"),
        TimingsJson = r.Str("timings_json"),
    };
}
