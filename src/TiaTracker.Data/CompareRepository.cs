using System.Globalization;
using TiaTracker.Core.Compare;
using TiaTracker.Core.Reconcile;
using TiaTracker.Core.Snapshots;

namespace TiaTracker.Data;

public sealed record CompareRunInfo(long Id, long BaseSnapshotId, long TargetSnapshotId, DateTime CreatedUtc, string? Summary);

/// <summary>
/// Confronti salvati (prova di uno stato: evidence_run_id) e decisioni della
/// riconciliazione, riapplicate ai confronti successivi della stessa versione.
/// </summary>
public sealed class CompareRepository
{
    private readonly Db _db;

    public CompareRepository(Db db)
    {
        _db = db;
    }

    public long SaveRun(long baseSnapshotId, long targetSnapshotId, IReadOnlyList<CompareEntry> entries, string? summary)
    {
        using Db.Tx tx = _db.Begin();
        long runId = _db.Insert(
            "INSERT INTO compare_run (base_snapshot_id, target_snapshot_id, created_utc, norm_version, summary_json) VALUES ($b, $t, $u, $n, $s)",
            ("$b", baseSnapshotId), ("$t", targetSnapshotId), ("$u", DateTime.UtcNow), ("$n", XmlCanonicalizer.NormVersion), ("$s", summary));

        foreach (CompareEntry e in entries.Where(e => e.Status != CompareStatus.Unchanged))
        {
            _db.Exec(
                @"INSERT INTO compare_item (run_id, family, kind, name, old_name, status, moved, facets, parent_name, similarity, base_item_id, target_item_id)
                  VALUES ($r, $f, $k, $n, $o, $s, $m, $fa, $p, $sim, $bi, $ti)",
                ("$r", runId), ("$f", e.Family), ("$k", e.Kind), ("$n", e.Name), ("$o", e.OldName), ("$s", e.Status.ToString()),
                ("$m", e.Moved), ("$fa", ((int)e.Facets).ToString(CultureInfo.InvariantCulture)), ("$p", e.ParentName),
                ("$sim", e.Similarity), ("$bi", e.Base?.Id), ("$ti", e.Target?.Id));
        }

        tx.Commit();
        return runId;
    }

    public List<CompareRunInfo> RunsForVersion(long versionId) =>
        _db.Query(
            @"SELECT r.* FROM compare_run r JOIN snapshot s ON s.id = r.target_snapshot_id
              WHERE s.version_id = $v ORDER BY r.created_utc DESC",
            r => new CompareRunInfo(r.L("id"), r.L("base_snapshot_id"), r.L("target_snapshot_id"), r.Utc("created_utc") ?? default,
                r.Str("summary_json")),
            ("$v", versionId));

    public List<ReconcileLinkInfo> Links(long versionId) =>
        _db.Query("SELECT * FROM reconcile_link WHERE version_id = $v",
            r => new ReconcileLinkInfo(r.S("block_family"), r.S("block_name"), r.LN("change_id"), r.S("decision")),
            ("$v", versionId));

    public void SetLink(long versionId, string family, string blockName, long? changeId, string decision, string? note = null)
    {
        using Db.Tx tx = _db.Begin();
        RemoveLinks(versionId, family, blockName);
        _db.Exec(
            "INSERT INTO reconcile_link (version_id, block_family, block_name, change_id, decision, utc, note) VALUES ($v, $f, $n, $c, $d, $u, $note)",
            ("$v", versionId), ("$f", family), ("$n", blockName), ("$c", changeId), ("$d", decision), ("$u", DateTime.UtcNow), ("$note", note));
        tx.Commit();
    }

    public void RemoveLinks(long versionId, string family, string blockName)
    {
        _db.Exec("DELETE FROM reconcile_link WHERE version_id = $v AND block_family = $f AND block_name = $n",
            ("$v", versionId), ("$f", family), ("$n", blockName));
    }
}
