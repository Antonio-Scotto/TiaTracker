namespace TiaTracker.Data;

public sealed record ImportSourceInfo(long Id, string RelPath, string Sha256, DateTime ImportedUtc, string? Confidence, string Status);

/// <summary>File storici gia' importati, riconosciuti dallo SHA-256 per non reimportarli.</summary>
public sealed class ImportRepository
{
    private readonly Db _db;

    public ImportRepository(Db db)
    {
        _db = db;
    }

    public bool IsImported(long commessaId, string sha256) =>
        _db.Scalar("SELECT 1 FROM import_source WHERE commessa_id = $c AND sha256 = $s", ("$c", commessaId), ("$s", sha256)) != null;

    public long AddSource(long commessaId, string relPath, string sha256, string? confidence)
    {
        object? existing = _db.Scalar("SELECT id FROM import_source WHERE commessa_id = $c AND sha256 = $s", ("$c", commessaId), ("$s", sha256));
        if (existing != null)
        {
            return Convert.ToInt64(existing, System.Globalization.CultureInfo.InvariantCulture);
        }

        return _db.Insert(
            "INSERT INTO import_source (commessa_id, rel_path, sha256, imported_utc, confidence, status) VALUES ($c, $p, $s, $u, $conf, 'draft')",
            ("$c", commessaId), ("$p", relPath), ("$s", sha256), ("$u", DateTime.UtcNow), ("$conf", confidence));
    }

    public List<ImportSourceInfo> Sources(long commessaId) =>
        _db.Query("SELECT * FROM import_source WHERE commessa_id = $c ORDER BY imported_utc DESC",
            r => new ImportSourceInfo(r.L("id"), r.S("rel_path"), r.S("sha256"), r.Utc("imported_utc") ?? default, r.Str("confidence"), r.S("status")),
            ("$c", commessaId));
}
