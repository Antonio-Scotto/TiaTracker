using Microsoft.Data.Sqlite;
using TiaTracker.Data;

namespace TiaTracker.Core.Tests;

public class DbMaintenanceTests
{
    /// <summary>
    /// Ogni colonna che punta a un'altra tabella ha un indice che comincia da lei: senza,
    /// cancellare una riga del padre (snapshot, contenuti orfani) scorre tutta la tabella figlia.
    /// </summary>
    [Fact]
    public void OgniChiaveEsternaHaUnIndice()
    {
        using Db db = Db.OpenInMemory();
        List<string> tables = db.Query("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'", r => r.S("name")!);
        List<string> missing = new();
        foreach (string table in tables)
        {
            List<string> fks = db.Query("SELECT \"from\" AS col FROM pragma_foreign_key_list($t)", r => r.S("col")!, ("$t", table));
            HashSet<string> leading = db.Query(
                    "SELECT (SELECT name FROM pragma_index_info(il.name) WHERE seqno = 0) AS col FROM pragma_index_list($t) il",
                    r => r.S("col") ?? "", ("$t", table))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string fk in fks.Where(fk => !leading.Contains(fk)))
            {
                missing.Add(table + "." + fk);
            }
        }

        Assert.True(missing.Count == 0, "Senza indice: " + string.Join(", ", missing));
    }

    [Fact]
    public void IlBackupEUnArchivioCompletoECompatto()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tiatracker_test_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string backup;
            using (Db db = Db.OpenInMemory())
            {
                db.Exec("INSERT INTO commessa (code, name, created_utc) VALUES ('AUT000001', 'Demo', '2026-01-01T00:00:00Z')");
                backup = db.Backup(dir);
            }

            Assert.True(File.Exists(backup));
            using SqliteConnection conn = new("Data Source=" + backup + ";Pooling=False");
            conn.Open();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM commessa; ";
            Assert.Equal(1L, cmd.ExecuteScalar());
            cmd.CommandText = "PRAGMA user_version;";
            Assert.Equal((long)Db.LatestSchemaVersion, cmd.ExecuteScalar());
            cmd.CommandText = "PRAGMA freelist_count;";
            Assert.Equal(0L, cmd.ExecuteScalar());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
