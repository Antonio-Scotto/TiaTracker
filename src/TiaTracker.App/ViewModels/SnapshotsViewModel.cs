using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TiaTracker.App.Services;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Scanning;

namespace TiaTracker.App.ViewModels;

public sealed class SnapshotRow
{
    public SnapshotRow(Snapshot s)
    {
        Snapshot = s;
    }

    public Snapshot Snapshot { get; }

    public string When => s.CreatedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    private Snapshot s => Snapshot;

    public string Source => s.Source switch
    {
        SnapshotSources.Attach => "TIA aperto (" + s.SourceDetail + ")",
        SnapshotSources.File => "file",
        SnapshotSources.Backup => "backup " + Path.GetFileName(s.SourceDetail ?? ""),
        SnapshotSources.Rar => "rar",
        SnapshotSources.Export => "cartella " + Path.GetFileName(Path.GetDirectoryName(s.SourceDetail ?? "") ?? "") + "\\" + Path.GetFileName(s.SourceDetail ?? ""),
        _ => s.Source,
    };

    public string? Plc => s.PlcName;

    public string Items => s.ItemCount + (s.FailedCount > 0 ? " (" + s.FailedCount + " falliti)" : "");

    public string Modified => s.ProjectModified switch
    {
        true => "NON salvato",
        false => "salvato",
        _ => "",
    };

    public bool Partial => s.Partial;

    public string Timings
    {
        get
        {
            if (string.IsNullOrEmpty(s.TimingsJson))
            {
                return "";
            }

            try
            {
                Dictionary<string, long>? t = JsonSerializer.Deserialize<Dictionary<string, long>>(s.TimingsJson);
                return t == null ? "" : string.Join("  ", t.Select(kv => kv.Key + " " + (kv.Value / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "s"));
            }
            catch (JsonException)
            {
                return "";
            }
        }
    }

    public string Display => When + " · " + Plc + " · " + Source;

    public override string ToString() => Display;
}

/// <summary>Scheda Snapshot: elenco e creazione da TIA aperto, da file, da backup o rar.</summary>
public sealed partial class SnapshotsViewModel : ObservableObject
{
    private readonly VersionDetailViewModel _owner;

    public SnapshotsViewModel(VersionDetailViewModel owner)
    {
        _owner = owner;
    }

    public ObservableCollection<SnapshotRow> Rows { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    public partial SnapshotRow? Selected { get; set; }

    [ObservableProperty]
    public partial string Hint { get; set; } = "";

    private ProjectVersion Version => _owner.Version;

    private bool IsOpen => _owner.Node.Status.Open.Kind == OpenKind.Open;

    public bool CanFromOpen => IsOpen && !WorkerRunner.Busy;

    public bool CanFromFile => Version.HasFolder && !IsOpen && !WorkerRunner.Busy;

    public bool CanFromBackup => Version.HasBackup && !WorkerRunner.Busy;

    public bool CanFromRar => Version.HasRar && !WorkerRunner.Busy;

    public void Reload()
    {
        Rows.Clear();
        foreach (Snapshot s in AppServices.Snapshots.ByVersion(Version.Id))
        {
            Rows.Add(new SnapshotRow(s));
        }

        Hint = IsOpen
            ? "La versione e' aperta in TIA: snapshot dal TIA aperto (attach). Se ci sono modifiche non salvate lo snapshot le include e lo segnala."
            : Version.HasFolder
                ? "Versione chiusa: lo snapshot si fa su una copia della cartella, aperta da TIA senza interfaccia (1-2 minuti di avvio)."
                : "Solo archivio: lo snapshot si fa estraendo il backup o il rar in una cartella di lavoro.";
        OnPropertyChanged(nameof(CanFromOpen));
        OnPropertyChanged(nameof(CanFromFile));
        OnPropertyChanged(nameof(CanFromBackup));
        OnPropertyChanged(nameof(CanFromRar));
    }

    [RelayCommand]
    private Task FromOpen() => _owner.Actions.SnapshotFromOpen();

    [RelayCommand]
    private Task FromFile() => _owner.Actions.SnapshotFromFolder();

    [RelayCommand]
    private Task FromBackup()
    {
        string? zip = _owner.Actions.NewestBackupZip();
        if (zip == null)
        {
            Notify.Info("Snapshot dal backup", "Nessuno zip nella cartella di backup.");
            return Task.CompletedTask;
        }

        return Run("Snapshot dal backup " + Path.GetFileName(zip), (job, ct) =>
            SnapshotService.FromArchive(Version, zip, SnapshotSources.Backup, job, ct));
    }

    [RelayCommand]
    private Task FromRar()
    {
        string? rar = _owner.RarFull;
        return rar == null
            ? Task.CompletedTask
            : Run("Snapshot dal rar", (job, ct) => SnapshotService.FromArchive(Version, rar, SnapshotSources.Rar, job, ct));
    }

    /// <summary>
    /// Snapshot da una cartella di export XML gia' esistente (es. _tia\...\xml):
    /// registra a posteriori la storia senza aprire TIA.
    /// </summary>
    [RelayCommand]
    private Task FromExportFolder()
    {
        // I percorsi in _tia sono lunghi: si incollano; vuoto = selettore di cartelle.
        string? folder = Views.TextPromptWindow.Ask("Snapshot da cartella XML",
            "Percorso della cartella con gli XML esportati (una sottocartella per gruppo, es. _tia\\...\\xml).\n" +
            "Lasciare vuoto per sceglierla con il selettore.", "");
        if (folder == null)
        {
            return Task.CompletedTask;
        }

        folder = folder.Trim().Trim('"');
        if (folder.Length == 0)
        {
            Microsoft.Win32.OpenFolderDialog dialog = new() { Title = "Cartella con gli XML esportati (una sottocartella per gruppo)" };
            string? start = _owner.Root?.Path;
            if (start != null && Directory.Exists(Path.Combine(start, "_tia")))
            {
                dialog.InitialDirectory = Path.Combine(start, "_tia");
            }

            if (dialog.ShowDialog() != true)
            {
                return Task.CompletedTask;
            }

            folder = dialog.FolderName;
        }

        if (!Directory.Exists(folder))
        {
            Notify.Warning("Snapshot da cartella XML", "Cartella non trovata: " + folder);
            return Task.CompletedTask;
        }

        string defaultPlc = AppServices.Snapshots.ByCommessa(Version.CommessaId).Select(s => s.PlcName).FirstOrDefault(p => p != null) ?? "PLC_1";
        string? plc = Views.TextPromptWindow.Ask("PLC dello snapshot",
            "Nome del PLC a cui appartiene l'export (serve a confrontarlo con gli snapshot dello stesso PLC):", defaultPlc);
        if (plc == null)
        {
            return Task.CompletedTask;
        }

        return Run("Snapshot dalla cartella " + Path.GetFileName(folder), (job, _) =>
            SnapshotService.FromExportFolder(Version, folder, string.IsNullOrWhiteSpace(plc) ? null : plc.Trim(), job));
    }

    /// <summary>Usato da "Nuovo snapshot e confronta".</summary>
    public Task RunFromCompare(bool fromOpenTia) => fromOpenTia ? FromOpen() : FromFile();

    private async Task Run(string title, Func<JobState, CancellationToken, Task<SnapshotService.Outcome>> work)
    {
        Reload();
        await JobRunner.RunAsync(title, async (job, ct) =>
        {
            SnapshotService.Outcome r = await work(job, ct);
            return r.Ok
                ? JobOutcome.Done(r.Message)
                : r.NeedsDiagnostics
                    ? JobOutcome.Failed(r.Message, JobRunner.DiagnosticsAction())
                    : JobOutcome.Failed(r.Message);
        });
        _owner.Main.Reload(_owner.Node.Parent);
    }

    private bool CanDelete() => Selected != null;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (Selected == null)
        {
            return;
        }

        if (MessageBox.Show("Eliminare lo snapshot del " + Selected.When + " (" + Selected.Plc + ")?\nI confronti che lo usano vengono eliminati.",
                "Elimina snapshot", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        AppServices.Snapshots.Delete(Selected.Snapshot.Id);
        _owner.Refresh();
    }
}
