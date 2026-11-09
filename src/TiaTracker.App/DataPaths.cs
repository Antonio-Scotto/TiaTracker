namespace TiaTracker.App;

/// <summary>
/// Dove stanno i dati dell'app. Priorita': variabile TIATRACKER_DATA, poi
/// portable.flag accanto all'exe (cartella data\), poi %LOCALAPPDATA%\TiaTracker.
/// Il default sta fuori da OneDrive e dal Desktop e sopravvive agli
/// aggiornamenti dell'app.
/// </summary>
public static class DataPaths
{
    public const string EnvVar = "TIATRACKER_DATA";
    public const string PortableFlag = "portable.flag";

    static DataPaths()
    {
        AppDir = AppContext.BaseDirectory;
        string? env = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(env))
        {
            Root = Path.GetFullPath(env);
            Mode = "variabile " + EnvVar;
        }
        else if (File.Exists(Path.Combine(AppDir, PortableFlag)))
        {
            Root = Path.Combine(AppDir, "data");
            Mode = "portabile";
        }
        else
        {
            Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TiaTracker");
            Mode = "profilo utente";
        }
    }

    public static string AppDir { get; }

    public static string Root { get; }

    public static string Mode { get; }

    public static string Database => Path.Combine(Root, "tiatracker.db");

    public static string Settings => Path.Combine(Root, "settings.json");

    public static string Logs => Path.Combine(Root, "logs");

    public static string Work => Path.Combine(Root, "work");

    public static string Backup => Path.Combine(Root, "backup");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Work);
        Directory.CreateDirectory(Backup);
    }
}
