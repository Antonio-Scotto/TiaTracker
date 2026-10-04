using TiaTracker.Data;

namespace TiaTracker.App.Services;

/// <summary>
/// Le poche istanze condivise dell'app. Nessun contenitore DI: una
/// finestra, un DB, un job alla volta.
/// </summary>
public static class AppServices
{
    private static Db? _db;

    public static Db Db => _db ?? throw new InvalidOperationException("DB non aperto");

    public static AppSettings Settings { get; private set; } = new();

    public static CommessaRepository Commesse { get; private set; } = null!;

    public static VersionRepository Versions { get; private set; } = null!;

    public static ChangeRepository Changes { get; private set; } = null!;

    public static SnapshotRepository Snapshots { get; private set; } = null!;

    public static CompareRepository Compares { get; private set; } = null!;

    public static ImportRepository Imports { get; private set; } = null!;

    public static ContentStore Content { get; private set; } = null!;

    public static HardwareRepository Hardware { get; private set; } = null!;

    public static DocRepository Docs { get; private set; } = null!;

    public static PlanRepository Plans { get; private set; } = null!;

    public static void Start()
    {
        DataPaths.EnsureCreated();
        Settings = AppSettings.Load(DataPaths.Settings);
        Log.Info("Avvio TiaTracker " + typeof(AppServices).Assembly.GetName().Version + ", dati in " + DataPaths.Root +
                 " (" + DataPaths.Mode + ")");

        _db = Db.Open(DataPaths.Database);
#if DEBUG
        // In sviluppo un accesso al DB fuori dal thread UI e' un errore subito visibile.
        _db.Strict = true;
#endif
        Db.OnWrongThread = Log.Warn;
        Commesse = new CommessaRepository(_db);
        Versions = new VersionRepository(_db);
        Changes = new ChangeRepository(_db);
        Snapshots = new SnapshotRepository(_db);
        Compares = new CompareRepository(_db);
        Imports = new ImportRepository(_db);
        Content = new ContentStore(_db);
        Hardware = new HardwareRepository(_db);
        Docs = new DocRepository(_db);
        Plans = new PlanRepository(_db);
        ProjectCopier.PurgeOld();

        // Una copia al giorno, le ultime 7.
        if (Settings.LastBackupUtc == null || DateTime.UtcNow - Settings.LastBackupUtc.Value > TimeSpan.FromHours(20))
        {
            try
            {
                string file = _db.Backup(DataPaths.Backup);
                Settings.LastBackupUtc = DateTime.UtcNow;
                SaveSettings();
                Log.Info("Backup DB: " + file);
            }
            catch (Exception ex)
            {
                Log.Error("Backup DB fallito", ex);
            }
        }
    }

    public static void SaveSettings() => Settings.Save(DataPaths.Settings);

    public static void Stop()
    {
        SaveSettings();
        _db?.Dispose();
        _db = null;
    }
}
