using System.Text.Json;

namespace TiaTracker.App.Services;

/// <summary>
/// settings.json: solo cio' che dipende dal PC (percorsi, override). I dati
/// veri stanno nel DB.
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Vecchia impostazione unica: vale per il worker V21 se TiaPublicApiV21 e' vuota.</summary>
    public string? TiaPublicApi { get; set; }

    /// <summary>Cartella con Siemens.Engineering.Base.dll per il worker V21 (vuoto = ricerca automatica).</summary>
    public string? TiaPublicApiV21 { get; set; }

    /// <summary>Cartella con Siemens.Engineering.dll per il worker V18 (vuoto = ricerca automatica).</summary>
    public string? TiaPublicApiV18 { get; set; }

    /// <summary>Cartella dei worker se non stanno in worker\ accanto all'exe (sviluppo).</summary>
    public string? WorkerDir { get; set; }

    /// <summary>Rar.exe se WinRAR non e' in Programmi.</summary>
    public string? RarPath { get; set; }

    public string? LastFolder { get; set; }

    public long? LastVersionId { get; set; }

    public long? LastCommessaId { get; set; }

    public DateTime? LastBackupUtc { get; set; }

    /// <summary>Tieni la cartella di lavoro dello snapshot (XML/SCL grezzi) per diagnosi.</summary>
    public bool KeepWorkFolders { get; set; }

    /// <summary>Albero: mostra anche le versioni vecchie, archiviate e scartate.</summary>
    public bool ShowRetiredVersions { get; set; } = true;

    /// <summary>Scansione automatica delle cartelle delle versioni ogni N minuti (0 = solo a mano).</summary>
    public int AutoScanMinutes { get; set; } = 10;

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Warn("settings.json illeggibile, uso i valori predefiniti: " + ex.Message);
        }

        return new AppSettings();
    }

    public void Save(string path)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch (IOException ex)
        {
            Log.Warn("settings.json non salvato: " + ex.Message);
        }
    }
}
