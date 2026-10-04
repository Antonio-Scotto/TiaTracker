using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace TiaTracker.Data;

/// <summary>
/// Connessione unica all'archivio SQLite, migrazioni incorporate con
/// PRAGMA user_version e copie di sicurezza. SQL scritto a mano.
/// </summary>
public sealed class Db : IDisposable
{
    private readonly int _ownerThread;

    private Db(SqliteConnection connection, string path)
    {
        Connection = connection;
        Path = path;
        _ownerThread = Environment.CurrentManagedThreadId;
    }

    public SqliteConnection Connection { get; }

    public string Path { get; }

    /// <summary>
    /// Una connessione sola e una transazione corrente (_current) non thread-safe:
    /// il DB si usa solo dal thread che l'ha aperto (il thread UI). Con Strict un
    /// accesso da un altro thread e' un'eccezione (build di sviluppo e test),
    /// altrimenti finisce in OnWrongThread (log).
    /// </summary>
    public bool Strict { get; set; }

    public static Action<string>? OnWrongThread { get; set; }

    public static Db Open(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        SqliteConnectionStringBuilder csb = new()
        {
            DataSource = path,
            ForeignKeys = true,
            Pooling = false,
        };

        SqliteConnection conn = new(csb.ToString());
        conn.Open();
        Db db = new(conn, path);
        db.Exec("PRAGMA journal_mode = WAL;");
        db.Exec("PRAGMA busy_timeout = 5000;");
        db.Migrate();
        return db;
    }

    public static Db OpenInMemory() => OpenInMemory(int.MaxValue);

    /// <summary>DB in memoria fermo alla migrazione <paramref name="maxVersion"/>: per provare le migrazioni successive.</summary>
    internal static Db OpenInMemory(int maxVersion)
    {
        SqliteConnection conn = new("Data Source=:memory:;Foreign Keys=True");
        conn.Open();
        Db db = new(conn, ":memory:") { Strict = true };
        db.Migrate(maxVersion);
        return db;
    }

    public int SchemaVersion => Convert.ToInt32(Scalar("PRAGMA user_version;"), CultureInfo.InvariantCulture);

    /// <summary>Numero dell'ultima migrazione incorporata.</summary>
    public static int LatestSchemaVersion => Migrations().Max(m => m.Number);

    private static List<(int Number, string Name)> Migrations() =>
        typeof(Db).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("Migrations.", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n => (int.Parse(n.Substring("Migrations.".Length, 3), CultureInfo.InvariantCulture), n))
            .OrderBy(m => m.Item1)
            .ToList();

    /// <summary>Applica le migrazioni mancanti (fino a <paramref name="maxVersion"/>).</summary>
    internal void Migrate(int maxVersion = int.MaxValue)
    {
        Assembly asm = typeof(Db).Assembly;
        int current = SchemaVersion;
        foreach ((int number, string name) in Migrations())
        {
            if (number <= current || number > maxVersion)
            {
                continue;
            }

            using Stream stream = asm.GetManifestResourceStream(name)!;
            using StreamReader reader = new(stream);
            string sql = reader.ReadToEnd();

            using SqliteTransaction tx = Connection.BeginTransaction();
            using (SqliteCommand cmd = Connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql + "\nPRAGMA user_version = " + number.ToString(CultureInfo.InvariantCulture) + ";";
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    /// <summary>
    /// Copia consistente del DB (API di backup SQLite, non una copia di file)
    /// in backup\, tenendo le ultime <paramref name="keep"/>.
    /// </summary>
    public string Backup(string backupDir, int keep = 7)
    {
        Directory.CreateDirectory(backupDir);
        string target = System.IO.Path.Combine(backupDir,
            "tiatracker_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".db");
        using (SqliteConnection dest = new("Data Source=" + target + ";Pooling=False"))
        {
            dest.Open();
            Connection.BackupDatabase(dest);
        }

        foreach (FileInfo old in new DirectoryInfo(backupDir).GetFiles("tiatracker_*.db")
                     .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                     .Skip(keep))
        {
            try
            {
                old.Delete();
            }
            catch (IOException)
            {
            }
        }

        return target;
    }

    // ---------- helper ----------

    private SqliteTransaction? _current;

    /// <summary>
    /// Transazione corrente: finche' e' aperta ogni comando la usa da solo
    /// (Microsoft.Data.Sqlite rifiuta comandi senza transazione su una connessione che ne ha una).
    /// </summary>
    public Tx Begin()
    {
        CheckThread();

        // Gia' dentro una transazione: ci si unisce, commit e rollback restano a chi l'ha aperta.
        if (_current != null)
        {
            return new Tx(this, null);
        }

        _current = Connection.BeginTransaction();
        return new Tx(this, _current);
    }

    public bool InTransaction => _current != null;

    public sealed class Tx : IDisposable
    {
        private readonly Db _db;
        private readonly SqliteTransaction? _tx;

        internal Tx(Db db, SqliteTransaction? tx)
        {
            _db = db;
            _tx = tx;
        }

        public void Commit() => _tx?.Commit();

        public void Dispose()
        {
            if (_tx == null)
            {
                return;
            }

            _tx.Dispose();
            _db._current = null;
        }
    }

    public int Exec(string sql, params (string Name, object? Value)[] args) => Exec(null, sql, args);

    public long Insert(string sql, params (string Name, object? Value)[] args) => Insert(null, sql, args);

    public int Exec(SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        using SqliteCommand cmd = Command(tx, sql, args);
        return cmd.ExecuteNonQuery();
    }

    public long Insert(SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        using SqliteCommand cmd = Command(tx, sql + "; SELECT last_insert_rowid();", args);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        using SqliteCommand cmd = Command(null, sql, args);
        object? v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        using SqliteCommand cmd = Command(null, sql, args);
        using SqliteDataReader r = cmd.ExecuteReader();
        List<T> result = new();
        while (r.Read())
        {
            result.Add(map(r));
        }

        return result;
    }

    /// <summary>Comando preparato per inserimenti in blocco, gia' legato alla transazione corrente.</summary>
    public SqliteCommand CreateCommand(string sql) => Command(null, sql, Array.Empty<(string, object?)>());

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId == _ownerThread)
        {
            return;
        }

        string message = "Accesso al DB dal thread " + Environment.CurrentManagedThreadId + " invece del thread UI " + _ownerThread +
                         ": i lavori in background devono restituire dati e lasciare il DB al thread UI.";
        if (Strict)
        {
            throw new InvalidOperationException(message);
        }

        OnWrongThread?.Invoke(message + Environment.NewLine + Environment.StackTrace);
    }

    private SqliteCommand Command(SqliteTransaction? tx, string sql, (string Name, object? Value)[] args)
    {
        CheckThread();
        SqliteCommand cmd = Connection.CreateCommand();
        cmd.Transaction = tx ?? _current;
        cmd.CommandText = sql;
        foreach ((string name, object? value) in args)
        {
            cmd.Parameters.AddWithValue(name, ToDb(value));
        }

        return cmd;
    }

    private static object ToDb(object? value) => value switch
    {
        null => DBNull.Value,
        bool b => b ? 1 : 0,
        DateTime dt => Iso(dt)!,
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Enum e => e.ToString(),
        _ => value,
    };

    public static string? Iso(DateTime? value)
    {
        if (value == null)
        {
            return null;
        }

        DateTime utc = value.Value.Kind == DateTimeKind.Local ? value.Value.ToUniversalTime() : value.Value;
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    public static DateTime? ParseIso(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return DateTime.Parse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    }

    public void Dispose()
    {
        Connection.Dispose();
    }
}

internal static class ReaderExtensions
{
    public static string? Str(this SqliteDataReader r, string name)
    {
        int i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    public static string S(this SqliteDataReader r, string name) => r.Str(name) ?? "";

    public static long L(this SqliteDataReader r, string name) => r.GetInt64(r.GetOrdinal(name));

    public static long? LN(this SqliteDataReader r, string name)
    {
        int i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : r.GetInt64(i);
    }

    public static int? IN(this SqliteDataReader r, string name)
    {
        int i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : r.GetInt32(i);
    }

    public static bool B(this SqliteDataReader r, string name)
    {
        int i = r.GetOrdinal(name);
        return !r.IsDBNull(i) && r.GetInt64(i) != 0;
    }

    public static bool? BN(this SqliteDataReader r, string name)
    {
        int i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : r.GetInt64(i) != 0;
    }

    public static double? DN(this SqliteDataReader r, string name)
    {
        int i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : r.GetDouble(i);
    }

    public static DateTime? Utc(this SqliteDataReader r, string name) => Db.ParseIso(r.Str(name));

    public static DateOnly? Date(this SqliteDataReader r, string name)
    {
        string? s = r.Str(name);
        return s == null ? null : DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
