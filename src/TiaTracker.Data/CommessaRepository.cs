using Microsoft.Data.Sqlite;
using TiaTracker.Core.Domain;

namespace TiaTracker.Data;

public sealed class CommessaRepository
{
    private readonly Db _db;

    public CommessaRepository(Db db)
    {
        _db = db;
    }

    public List<Commessa> All() =>
        _db.Query("SELECT * FROM commessa ORDER BY code", Map);

    public Commessa? Get(long id) =>
        _db.Query("SELECT * FROM commessa WHERE id = $id", Map, ("$id", id)).FirstOrDefault();

    public long Insert(Commessa c)
    {
        c.CreatedUtc = c.CreatedUtc == default ? DateTime.UtcNow : c.CreatedUtc;
        c.Id = _db.Insert(
            @"INSERT INTO commessa (code, name, customer, note, created_utc, output_dir, backup_dir, settings_json)
              VALUES ($code, $name, $cust, $note, $created, $out, $bak, $set)",
            ("$code", c.Code.Trim()), ("$name", c.Name.Trim()), ("$cust", c.Customer), ("$note", c.Note), ("$created", c.CreatedUtc),
            ("$out", Blank(c.OutputDir)), ("$bak", Blank(c.BackupDir)), ("$set", Blank(c.SettingsJson)));
        return c.Id;
    }

    public void Update(Commessa c)
    {
        _db.Exec(
            @"UPDATE commessa SET code = $code, name = $name, customer = $cust, note = $note, output_dir = $out, backup_dir = $bak,
                settings_json = $set WHERE id = $id",
            ("$code", c.Code.Trim()), ("$name", c.Name.Trim()), ("$cust", c.Customer), ("$note", c.Note),
            ("$out", Blank(c.OutputDir)), ("$bak", Blank(c.BackupDir)), ("$set", Blank(c.SettingsJson)), ("$id", c.Id));
    }

    // ---------- layout IP ----------

    public List<IpDevice> IpDevices(long commessaId) =>
        _db.Query("SELECT * FROM ip_device WHERE commessa_id = $c ORDER BY sort, id", r => new IpDevice
        {
            Id = r.L("id"),
            CommessaId = r.L("commessa_id"),
            Sort = r.IN("sort") ?? 0,
            Network = r.Str("network"),
            Ip = r.Str("ip"),
            Subnet = r.Str("subnet"),
            Gateway = r.Str("gateway"),
            Name = r.Str("name"),
            Kind = r.Str("kind"),
            ProfinetName = r.Str("profinet_name"),
            Location = r.Str("location"),
            Mac = r.Str("mac"),
            Notes = r.Str("notes"),
            UpdatedUtc = r.Utc("updated_utc") ?? default,
            Source = r.Str("source") ?? IpSources.Manual,
            TiaKey = r.Str("tia_key"),
            TiaVersionId = r.LN("tia_version_id"),
            TiaSeenUtc = r.Utc("tia_seen_utc"),
            TiaMissing = r.B("tia_missing"),
        }, ("$c", commessaId));

    /// <summary>Sostituisce l'intera tabella IP della commessa (la griglia si salva in blocco).</summary>
    public void SaveIpDevices(long commessaId, IReadOnlyList<IpDevice> devices)
    {
        DateTime now = DateTime.UtcNow;
        using Db.Tx tx = _db.Begin();
        _db.Exec("DELETE FROM ip_device WHERE commessa_id = $c", ("$c", commessaId));
        int sort = 0;
        foreach (IpDevice d in devices)
        {
            d.CommessaId = commessaId;
            d.Sort = sort++;
            d.UpdatedUtc = d.UpdatedUtc == default ? now : d.UpdatedUtc;
            d.Id = _db.Insert(
                @"INSERT INTO ip_device (commessa_id, sort, network, ip, subnet, gateway, name, kind, profinet_name, location, mac, notes, updated_utc,
                    source, tia_key, tia_version_id, tia_seen_utc, tia_missing)
                  VALUES ($c, $s, $net, $ip, $sub, $gw, $n, $k, $pn, $loc, $mac, $notes, $u, $src, $tk, $tv, $ts, $tm)",
                ("$c", commessaId), ("$s", d.Sort), ("$net", Blank(d.Network)), ("$ip", Blank(d.Ip)), ("$sub", Blank(d.Subnet)),
                ("$gw", Blank(d.Gateway)), ("$n", Blank(d.Name)), ("$k", Blank(d.Kind)), ("$pn", Blank(d.ProfinetName)),
                ("$loc", Blank(d.Location)), ("$mac", Blank(d.Mac)), ("$notes", Blank(d.Notes)), ("$u", d.UpdatedUtc),
                ("$src", IpSources.Normalize(d.Source)), ("$tk", Blank(d.TiaKey)), ("$tv", d.TiaVersionId),
                ("$ts", d.TiaSeenUtc), ("$tm", d.TiaMissing));
        }

        tx.Commit();
    }

    public List<ScanRoot> ScanRoots(long commessaId) =>
        _db.Query("SELECT * FROM scan_root WHERE commessa_id = $c ORDER BY id", MapRoot, ("$c", commessaId));

    public ScanRoot? GetScanRoot(long id) =>
        _db.Query("SELECT * FROM scan_root WHERE id = $id", MapRoot, ("$id", id)).FirstOrDefault();

    public long InsertScanRoot(ScanRoot r)
    {
        r.Id = _db.Insert(
            "INSERT INTO scan_root (commessa_id, path, name_regex, history_globs, last_scan_utc) VALUES ($c, $p, $rx, $g, $ls)",
            ("$c", r.CommessaId), ("$p", r.Path), ("$rx", Blank(r.NameRegex)), ("$g", Blank(r.HistoryGlobs)), ("$ls", r.LastScanUtc));
        return r.Id;
    }

    public void UpdateScanRoot(ScanRoot r)
    {
        _db.Exec("UPDATE scan_root SET path = $p, name_regex = $rx, history_globs = $g, last_scan_utc = $ls WHERE id = $id",
            ("$p", r.Path), ("$rx", Blank(r.NameRegex)), ("$g", Blank(r.HistoryGlobs)), ("$ls", r.LastScanUtc), ("$id", r.Id));
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static Commessa Map(SqliteDataReader r) => new()
    {
        Id = r.L("id"),
        Code = r.S("code"),
        Name = r.S("name"),
        Customer = r.Str("customer"),
        Note = r.Str("note"),
        CreatedUtc = r.Utc("created_utc") ?? DateTime.UtcNow,
        OutputDir = r.Str("output_dir"),
        BackupDir = r.Str("backup_dir"),
        SettingsJson = r.Str("settings_json"),
    };

    private static ScanRoot MapRoot(SqliteDataReader r) => new()
    {
        Id = r.L("id"),
        CommessaId = r.L("commessa_id"),
        Path = r.S("path"),
        NameRegex = r.Str("name_regex"),
        HistoryGlobs = r.Str("history_globs"),
        LastScanUtc = r.Utc("last_scan_utc"),
    };
}
