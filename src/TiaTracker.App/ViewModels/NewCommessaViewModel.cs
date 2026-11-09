using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TiaTracker.App.Services;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Output;
using TiaTracker.Core.Scanning;

namespace TiaTracker.App.ViewModels;

/// <summary>Una versione trovata nella cartella, prima di creare la commessa.</summary>
public sealed class FoundVersionRow
{
    public FoundVersionRow(ScannedVersion scanned, bool openInTia)
    {
        Scanned = scanned;
        IsOpenInTia = openInTia;
    }

    public ScannedVersion Scanned { get; }

    public bool IsOpenInTia { get; }

    public string Label => Scanned.Parsed.Label;

    public string ProjectName => Scanned.ProjectName;

    public string Detail
    {
        get
        {
            List<string> parts = new();
            if (Scanned.TiaVersionText != null)
            {
                parts.Add("TIA " + Scanned.TiaVersionText);
            }

            if (Scanned.LastSavedUtc is DateTime saved)
            {
                parts.Add("salvata " + saved.ToLocalTime().ToString("dd/MM/yy HH:mm", CultureInfo.InvariantCulture));
            }

            parts.Add(Scanned.HasFolder ? "cartella" : Scanned.HasBackup ? "solo backup" : Scanned.HasRar ? "solo .rar" : "");
            return string.Join(" · ", parts.Where(p => p.Length > 0));
        }
    }

    public ChipVm? Chip => IsOpenInTia ? new ChipVm("Aperta in TIA", Tone.Orange)
        : !Scanned.HasFolder ? new ChipVm("Solo archivio", Tone.Muted)
        : null;

    /// <summary>Come si farebbe l'export iniziale di questa versione.</summary>
    public string? ExportMode =>
        IsOpenInTia ? "dal TIA aperto: blocchi, hardware, rete e tag in pochi minuti"
        : Scanned.HasFolder && Scanned.ProjectFileRel != null ? "dalla cartella, aperta da TIA senza interfaccia (qualche minuto)"
        : Scanned.HasBackup ? "dal backup .zip piu' recente (estrazione e apertura senza interfaccia)"
        : Scanned.HasRar ? "dal .rar (estrazione e apertura senza interfaccia)"
        : null;

    public override string ToString() => Label;
}

/// <summary>
/// Nuova commessa: cartella con anteprima delle versioni (scansione in
/// background, mai sul thread UI), dati, cartelle e automazioni (versione In
/// lavoro, export iniziale da TIA, documenti).
/// </summary>
public sealed partial class NewCommessaViewModel : ObservableObject
{
    private CancellationTokenSource? _scan;
    private bool _codeTouched;
    private bool _nameTouched;
    private bool _filling;

    public ObservableCollection<FoundVersionRow> Found { get; } = new();

    /// <summary>Le versioni dell'ultima scansione riuscita (si applicano al DB senza rileggere il disco).</summary>
    public List<ScannedVersion>? Scanned { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputHint))]
    public partial string Folder { get; set; } = "";

    [ObservableProperty]
    public partial string Code { get; set; } = "";

    [ObservableProperty]
    public partial string CommessaName { get; set; } = "";

    [ObservableProperty]
    public partial string Customer { get; set; } = "";

    [ObservableProperty]
    public partial string NameRegex { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputHint))]
    public partial string OutputDir { get; set; } = "";

    [ObservableProperty]
    public partial string BackupDir { get; set; } = "";

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial string ScanText { get; set; } = "Scegliere la cartella che contiene le versioni del progetto (es. ...\\Commessa\\PLC\\TIA21).";

    /// <summary>La versione su cui si lavora: In lavoro e oggetto dell'export iniziale.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportHint), nameof(CanExport))]
    public partial FoundVersionRow? WorkVersion { get; set; }

    [ObservableProperty]
    public partial bool SetInLavoro { get; set; } = true;

    [ObservableProperty]
    public partial bool InitialExport { get; set; } = true;

    [ObservableProperty]
    public partial bool AutoDocuments { get; set; } = true;

    [ObservableProperty]
    public partial string? Error { get; set; }

    public bool CanExport => WorkVersion?.ExportMode != null;

    public string ExportHint => WorkVersion == null
        ? "Nessuna versione trovata: l'export si potra' fare dopo dal menu della versione."
        : WorkVersion.ExportMode == null
            ? WorkVersion.Label + ": niente cartella ne' archivio da esportare."
            : WorkVersion.Label + " " + WorkVersion.ExportMode + ". Diventa la base dei confronti e dei documenti.";

    public string OutputHint => OutputDir.Trim().Length > 0
        ? "Cartella scelta"
        : Folder.Trim().Length > 0
            ? "Vuoto = " + Path.Combine(Folder.Trim(), OutputLayout.DefaultFolderName)
            : "Vuoto = TiaTrackerOut nella cartella delle versioni";

    partial void OnFolderChanged(string value) => ScheduleScan();

    partial void OnNameRegexChanged(string value) => ScheduleScan();

    partial void OnCodeChanged(string value)
    {
        Error = null;
        if (!_filling)
        {
            _codeTouched = true;
        }
    }

    partial void OnOutputDirChanged(string value) => Error = null;

    partial void OnCommessaNameChanged(string value)
    {
        if (!_filling)
        {
            _nameTouched = true;
        }
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        OpenFolderDialog dialog = new() { Title = "Cartella con le versioni del progetto TIA" };
        string? start = Directory.Exists(Folder) ? Folder : AppServices.Settings.LastFolder;
        if (!string.IsNullOrEmpty(start) && Directory.Exists(start))
        {
            dialog.InitialDirectory = start;
        }

        if (dialog.ShowDialog() == true)
        {
            Folder = dialog.FolderName;
            AppServices.Settings.LastFolder = Path.GetDirectoryName(dialog.FolderName);
        }
    }

    [RelayCommand]
    private void BrowseOutput()
    {
        OpenFolderDialog dialog = new() { Title = "Cartella TiaTrackerOut della commessa" };
        if (Directory.Exists(Folder))
        {
            dialog.InitialDirectory = Folder;
        }

        if (dialog.ShowDialog() == true)
        {
            OutputDir = dialog.FolderName;
        }
    }

    [RelayCommand]
    private void BrowseBackup()
    {
        OpenFolderDialog dialog = new() { Title = "Cartella di rete per i backup .rar" };
        if (dialog.ShowDialog() == true)
        {
            BackupDir = dialog.FolderName;
        }
    }

    /// <summary>Scansione con attesa breve: scrivendo il percorso non si legge il disco a ogni tasto.</summary>
    private async void ScheduleScan()
    {
        _scan?.Cancel();
        CancellationTokenSource cts = new();
        _scan = cts;
        string folder = Folder.Trim();
        string? regex = NameRegex.Trim().Length == 0 ? null : NameRegex.Trim();
        Error = null;
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            Found.Clear();
            Scanned = null;
            WorkVersion = null;
            IsScanning = false;
            ScanText = folder.Length == 0 ? "Scegliere la cartella che contiene le versioni del progetto." : "Cartella non trovata.";
            return;
        }

        if (regex != null)
        {
            try
            {
                _ = new Regex(regex);
            }
            catch (ArgumentException ex)
            {
                ScanText = "Regex non valida: " + ex.Message;
                return;
            }
        }

        try
        {
            await Task.Delay(350, cts.Token);
            IsScanning = true;
            ScanText = "Lettura della cartella...";
            (List<ScannedVersion> found, HashSet<string> open) = await Task.Run(() =>
            {
                List<ScannedVersion> list = ProjectFolderScanner.Scan(folder, regex);
                bool tia = ScanService.IsTiaRunning();
                HashSet<string> opened = new(StringComparer.OrdinalIgnoreCase);
                foreach (ScannedVersion v in list.Where(v => v.FolderRel != null))
                {
                    string dir = v.FolderRel!.Length == 0 ? folder : Path.Combine(folder, v.FolderRel);
                    if (OpenStateDetector.Detect(dir, v.ProjectName, tia, Environment.MachineName).Kind == OpenKind.Open)
                    {
                        opened.Add(v.ProjectName);
                    }
                }

                return (list, opened);
            }, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            Apply(folder, found, open);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("Anteprima nuova commessa: " + ex.Message);
            ScanText = "Errore di lettura: " + ex.Message;
        }
        finally
        {
            if (_scan == cts)
            {
                IsScanning = false;
            }
        }
    }

    private void Apply(string folder, List<ScannedVersion> found, HashSet<string> open)
    {
        Scanned = found;
        Found.Clear();
        foreach (ScannedVersion v in found.OrderByDescending(v => v.SortKey, StringComparer.Ordinal))
        {
            Found.Add(new FoundVersionRow(v, open.Contains(v.ProjectName)));
        }

        ScanText = found.Count switch
        {
            0 => "Nessuna versione trovata in questa cartella (progetti TIA, .rar o backup).",
            1 => "1 versione trovata.",
            _ => found.Count + " versioni trovate, dalla piu' recente.",
        };

        // La versione su cui si lavora: quella aperta in TIA, altrimenti la piu' recente con cartella.
        WorkVersion = Found.FirstOrDefault(r => r.IsOpenInTia) ?? Found.FirstOrDefault(r => r.Scanned.HasFolder) ?? Found.FirstOrDefault();

        _filling = true;
        try
        {
            if (!_codeTouched || Code.Trim().Length == 0)
            {
                string? code = found.Select(v => v.Parsed.ProjectCode).FirstOrDefault(c => c != null);
                if (code != null)
                {
                    Code = code;
                }
            }

            if (!_nameTouched || CommessaName.Trim().Length == 0)
            {
                Match m = Regex.Match(found.FirstOrDefault()?.ProjectName ?? "", @"^AUT\d{6}_([^_]+)_V", RegexOptions.IgnoreCase);
                CommessaName = m.Success ? m.Groups[1].Value.ToUpperInvariant() : GuessName(folder);
            }
        }
        finally
        {
            _filling = false;
        }
    }

    /// <summary>La cartella della commessa e' quella sotto "Lavori In Corso" (o l'ultima del percorso).</summary>
    private static string GuessName(string folder)
    {
        string[] parts = folder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        int idx = Array.FindIndex(parts, p => p.Equals("Lavori In Corso", StringComparison.OrdinalIgnoreCase));
        return idx >= 0 && idx + 1 < parts.Length ? parts[idx + 1] : parts.LastOrDefault() ?? "";
    }

    /// <summary>Controlli prima di creare: messaggio in <see cref="Error"/>, niente finestre.</summary>
    public bool Validate()
    {
        Error = null;
        if (!Directory.Exists(Folder.Trim()))
        {
            Error = "La cartella delle versioni non esiste.";
        }
        else if (IsScanning)
        {
            Error = "Attendere la fine della lettura della cartella.";
        }
        else if (Code.Trim().Length == 0)
        {
            Error = "Indicare il codice della commessa (es. AUT123456).";
        }
        else if (AppServices.Commesse.All().Any(c => c.Code.Equals(Code.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            Error = "Esiste gia' una commessa con codice " + Code.Trim() + ".";
        }
        else if (OutputDir.Trim().Length > 0 && !Path.IsPathFullyQualified(OutputDir.Trim()))
        {
            Error = "La cartella TiaTrackerOut deve essere un percorso completo.";
        }

        return Error == null;
    }
}
