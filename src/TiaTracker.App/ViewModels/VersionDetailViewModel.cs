using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TiaTracker.App.Services;
using TiaTracker.App.Views;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Scanning;

namespace TiaTracker.App.ViewModels;

public sealed partial class VersionDetailViewModel : ObservableObject
{
    public VersionDetailViewModel(VersionNode node, MainViewModel main)
    {
        Node = node;
        Main = main;
        Note = node.Version.Note;
        Actions = new VersionActions(main, node);
        Snapshots = new SnapshotsViewModel(this);
        Blocks = new BlocksViewModel(this);
        Compare = new CompareViewModel(this);
        Refresh();
    }

    public VersionNode Node { get; }

    public MainViewModel Main { get; }

    /// <summary>Le azioni sulla versione, le stesse del menu tasto destro.</summary>
    public VersionActions Actions { get; }

    /// <summary>Indice della scheda Confronta.</summary>
    public const int CompareTab = 3;

    public SnapshotsViewModel Snapshots { get; }

    public BlocksViewModel Blocks { get; }

    public CompareViewModel Compare { get; }

    public ProjectVersion Version => Node.Version;

    public ScanRoot? Root => Node.Parent.RootOf(Version);

    public string Title => Node.Parent.Commessa.Code + " · " + Version.Label;

    public string? FolderFull => Version.HasFolder ? ScanService.Full(Root, Version.FolderRel) : null;

    public string? ProjectFileFull => ScanService.Full(Root, Version.ProjectFileRel);

    public string? RarFull => Version.HasRar ? ScanService.Full(Root, Version.RarRel) : null;

    public string? BackupFull => Version.HasBackup ? ScanService.Full(Root, Version.BackupRel) : null;

    public string TiaText => Version.TiaVersionText ?? (Version.TiaMajor.HasValue ? "V" + Version.TiaMajor : "?");

    public string LastSavedText => Version.LastSavedUtc.HasValue ? Local(Version.LastSavedUtc.Value) : "—";

    public string BackupText => Version.HasBackup
        ? Version.BackupCount + " backup TIA" + (Version.LastBackupUtc.HasValue ? ", ultimo " + Local(Version.LastBackupUtc.Value) : "")
        : "nessuno";

    public string OpenText => Node.Status.Open.Describe();

    public bool IsOpen => Node.Status.Open.IsOpen;

    public string LoadedText => Node.Status.OnPlc
        ? "Sul PLC: " + Node.Status.LoadsText
        : "Non risulta su nessun PLC";

    public string StateText => Node.Status.ChipText.Length > 0 ? Node.Status.ChipText : "Nessuno stato";

    public string StateHint => Node.Status.ChipToolTip;

    public ChipVm? StateChip => Node.MainChip;

    public IReadOnlyList<ChipVm> Chips => Node.Chips;

    [ObservableProperty]
    public partial string? Note { get; set; }

    public ObservableCollection<VersionEvent> Events { get; } = new();

    public ObservableCollection<VersionChangeRow> Changes { get; } = new();

    [ObservableProperty]
    public partial VersionChangeRow? SelectedChange { get; set; }

    [ObservableProperty]
    public partial int SelectedTab { get; set; }

    /// <summary>Intestazione con i dati della versione: chiusa su Blocchi e Confronta, che hanno bisogno di spazio.</summary>
    [ObservableProperty]
    public partial bool HeaderExpanded { get; set; } = true;

    partial void OnSelectedTabChanged(int value) => HeaderExpanded = value is not (1 or 3);

    public IReadOnlyList<StateOption> StateOptions => StateOption.States;

    public void Refresh()
    {
        Events.Clear();
        foreach (VersionEvent e in AppServices.Versions.Events(Version.Id))
        {
            Events.Add(e);
        }

        Changes.Clear();
        foreach (Change c in AppServices.Changes.ByCommessa(Version.CommessaId))
        {
            ChangeVersion? cv = c.Versions.FirstOrDefault(v => v.VersionId == Version.Id);
            if (cv != null)
            {
                Changes.Add(new VersionChangeRow(c, cv) { StateChanged = () => Refresh() });
            }
        }

        foreach (string p in new[]
                 {
                     nameof(Title), nameof(FolderFull), nameof(ProjectFileFull), nameof(RarFull), nameof(BackupFull),
                     nameof(TiaText), nameof(LastSavedText), nameof(BackupText), nameof(OpenText), nameof(IsOpen),
                     nameof(LoadedText), nameof(Chips), nameof(StateText), nameof(StateHint), nameof(StateChip), nameof(Version),
                 })
        {
            OnPropertyChanged(p);
        }

        Snapshots.Reload();
        Blocks.Reload();
        Compare.Reload();
    }

    private static string Local(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    // ---------- file della versione ----------

    public bool HasProject => Actions.HasProject;

    [RelayCommand]
    private void OpenInTia() => Actions.OpenInTia();

    /// <summary>Copia della cartella con un nome nuovo: la versione successiva (o un duplicato).</summary>
    [RelayCommand]
    private Task NewVersionFrom() => Actions.NewVersionFrom();

    /// <summary>Cartella nel Cestino; la versione resta con la sua storia, segnata mancante.</summary>
    [RelayCommand]
    private void DeleteVersion() => Actions.DeleteVersion();

    /// <summary>Backup .rar della cartella progetto nella cartella di rete della commessa.</summary>
    [RelayCommand]
    private Task BackupRar() => Actions.BackupRar();

    [RelayCommand]
    private void SaveNote()
    {
        AppServices.Versions.SetNote(Version.Id, Note);
        Main.StatusText = "Nota salvata";
        Main.Reload(Node.Parent);
    }

    [RelayCommand]
    private void OpenFolder(string? path) => Actions.OpenFolder(path);

    // ---------- modifiche: si creano e si scrivono nella commessa; qui lettura, associazione e stato ----------

    /// <summary>Lettura della modifica: si cambiano solo le versioni associate e il loro stato.</summary>
    [RelayCommand]
    private void OpenChange(VersionChangeRow? row)
    {
        row ??= SelectedChange;
        if (row != null && ChangeEditing.Open(AppServices.Changes.Get(row.Change.Id)!, Version))
        {
            Refresh();
        }
    }

    /// <summary>Scheda Modifiche della commessa, dove le modifiche si creano e si correggono.</summary>
    [RelayCommand]
    private void ManageChanges()
    {
        long? changeId = SelectedChange?.Change.Id;
        Node.Parent.IsSelected = true;
        if (Main.Detail is CommessaDetailViewModel cd && cd.Node == Node.Parent)
        {
            cd.ShowChange(changeId);
        }
    }

    /// <summary>Associa a questa versione modifiche gia' registrate (la stessa patch su piu' versioni).</summary>
    [RelayCommand]
    private void AddExistingChange()
    {
        List<Change> candidates = AppServices.Changes.ByCommessa(Version.CommessaId)
            .Where(c => c.Versions.All(v => v.VersionId != Version.Id))
            .ToList();
        if (candidates.Count == 0)
        {
            Notify.Info("Associa modifiche", AppServices.Changes.ByCommessa(Version.CommessaId).Count == 0
                ? "La commessa non ha ancora modifiche: si creano nella scheda Modifiche della commessa."
                : "Tutte le modifiche della commessa sono gia' associate a questa versione.");
            return;
        }

        PickChangesWindow dialog = new(candidates) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        foreach (Change c in dialog.Selected)
        {
            AppServices.Changes.SetVersionState(c.Id, Version.Id, dialog.State);
        }

        Refresh();
    }

    [RelayCommand]
    private void RemoveChange(VersionChangeRow? row)
    {
        row ??= SelectedChange;
        if (row == null)
        {
            return;
        }

        if (MessageBox.Show("Togliere \"" + row.Change.Title + "\" da " + Version.Label + "?\nLa modifica resta nelle altre versioni.",
                "Togli dalla versione", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            AppServices.Changes.RemoveFromVersion(row.Change.Id, Version.Id);
            Refresh();
        }
    }
}
