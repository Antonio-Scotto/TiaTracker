using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TiaTracker.App.Services;
using TiaTracker.App.Views;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Status;

namespace TiaTracker.App.ViewModels;

public sealed partial class CommessaDetailViewModel : ObservableObject
{
    public const int DashboardTab = 0;
    public const int ChangesTab = 1;
    public const int MatrixTab = 2;
    public const int DocumentsTab = 3;
    public const int PlanningTab = 4;
    public const int NetworkTab = 5;
    public const int SettingsTab = 6;

    private List<CommessaChangeRow> _allRows = new();
    private bool _syncingSettings;

    public CommessaDetailViewModel(CommessaNode node, MainViewModel main)
    {
        Node = node;
        Main = main;
        Name = node.Commessa.Name;
        Customer = node.Commessa.Customer;
        Note = node.Commessa.Note;
        OutputDir = node.Commessa.OutputDir;
        BackupDir = node.Commessa.BackupDir;
        Matrix = new MatrixViewModel(node);
        Ip = new IpTableViewModel(node, main);
        Docs = new DocumentsViewModel(node, main);
        Plan = new PlanningViewModel(node, main);
        Dashboard = new DashboardViewModel(node, main, this);
        Refresh();
        _ready = true;
    }

    private readonly bool _ready;

    /// <summary>La scheda aperta (anche da codice: la dashboard porta alla pianificazione o ai documenti).</summary>
    [ObservableProperty]
    public partial int SelectedTab { get; set; }

    public DashboardViewModel Dashboard { get; }

    // ---------- impostazioni della commessa ----------

    public ObservableCollection<Choice<long?>> ReferenceChoices { get; } = new();

    /// <summary>Versione di riferimento (Lista IP, documenti, dashboard): automatica o scelta a mano.</summary>
    [ObservableProperty]
    public partial Choice<long?>? ReferenceChoice { get; set; }

    [ObservableProperty]
    public partial bool AutoIpSync { get; set; }

    [ObservableProperty]
    public partial bool AutoDocuments { get; set; }

    [ObservableProperty]
    public partial bool WorkOnWeekends { get; set; }

    /// <summary>Impostazione dell'app (tutte le commesse): scansione automatica ogni N minuti, 0 = solo a mano.</summary>
    [ObservableProperty]
    public partial int AutoScanMinutes { get; set; }

    partial void OnReferenceChoiceChanged(Choice<long?>? value) => SaveSettings();

    partial void OnAutoIpSyncChanged(bool value) => SaveSettings();

    partial void OnAutoDocumentsChanged(bool value) => SaveSettings();

    partial void OnWorkOnWeekendsChanged(bool value) => SaveSettings();

    partial void OnAutoScanMinutesChanged(int value)
    {
        if (_syncingSettings || !_ready || value < 0)
        {
            return;
        }

        AppServices.Settings.AutoScanMinutes = value;
        AppServices.SaveSettings();
        Main.ApplyAutoScanSetting();
        Main.StatusText = value == 0 ? "Scansione automatica spenta" : "Scansione automatica ogni " + value + " minuti";
    }

    private void LoadSettings()
    {
        _syncingSettings = true;
        try
        {
            CommessaSettings s = CommessaSettings.Parse(Node.Commessa.SettingsJson);
            ReferenceChoices.Clear();
            ProjectVersion? auto = ReferenceVersion.Pick(Node.Versions.Select(v => v.Version).ToList(), Node.CurrentLoads, null);
            ReferenceChoices.Add(new Choice<long?>(null, "Automatica" + (auto != null ? " (ora " + auto.Label + ")" : "") +
                                                         ": In lavoro, poi caricata sul PLC, poi la piu' recente"));
            foreach (VersionNode v in Node.Versions.Where(v => !v.Version.Missing))
            {
                ReferenceChoices.Add(new Choice<long?>(v.Version.Id, v.Label + (v.MainChip != null ? " · " + v.MainChip.Text : "")));
            }

            ReferenceChoice = ReferenceChoices.FirstOrDefault(c => c.Value == s.ReferenceVersionId) ?? ReferenceChoices[0];
            AutoIpSync = s.AutoIpSync;
            AutoDocuments = s.AutoDocuments;
            WorkOnWeekends = s.WorkOnWeekends;
            AutoScanMinutes = AppServices.Settings.AutoScanMinutes;
        }
        finally
        {
            _syncingSettings = false;
        }
    }

    private void SaveSettings()
    {
        if (_syncingSettings || !_ready)
        {
            return;
        }

        CommessaSettings s = CommessaSettings.Parse(Node.Commessa.SettingsJson);
        s.ReferenceVersionId = ReferenceChoice?.Value;
        s.AutoIpSync = AutoIpSync;
        s.AutoDocuments = AutoDocuments;
        s.WorkOnWeekends = WorkOnWeekends;
        Node.Commessa.SettingsJson = s.ToJson();
        AppServices.Commesse.Update(Node.Commessa);
        Main.StatusText = "Impostazioni della commessa salvate";
        Dashboard.Invalidate();
        Docs.Invalidate();
        Plan.Invalidate();
    }

    partial void OnOutputDirChanged(string? value)
    {
        if (_ready)
        {
            SaveCommessa();
        }
    }

    partial void OnBackupDirChanged(string? value)
    {
        if (_ready)
        {
            SaveCommessa();
        }
    }

    public IpTableViewModel Ip { get; }

    public DocumentsViewModel Docs { get; }

    public PlanningViewModel Plan { get; }

    /// <summary>Vuoto = TiaTrackerOut nella prima cartella di scansione.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputEffective))]
    public partial string? OutputDir { get; set; }

    [ObservableProperty]
    public partial string? BackupDir { get; set; }

    public string OutputEffective =>
        string.IsNullOrWhiteSpace(OutputDir)
            ? Core.Output.OutputLayout.For(new Commessa(), Node.Roots)?.Root ?? "(nessuna cartella di scansione)"
            : OutputDir!;

    [RelayCommand]
    private void BrowseOutput()
    {
        OpenFolderDialog dialog = new() { Title = "Cartella TiaTrackerOut della commessa" };
        if (dialog.ShowDialog() == true)
        {
            OutputDir = dialog.FolderName;
            SaveCommessa();
        }
    }

    [RelayCommand]
    private void BrowseBackup()
    {
        OpenFolderDialog dialog = new() { Title = "Cartella di rete per i backup .rar" };
        if (!string.IsNullOrWhiteSpace(BackupDir) && Directory.Exists(BackupDir))
        {
            dialog.InitialDirectory = BackupDir;
        }

        if (dialog.ShowDialog() == true)
        {
            BackupDir = dialog.FolderName;
            SaveCommessa();
        }
    }

    [RelayCommand]
    private void OpenPath(string? which)
    {
        string? path = which == "backup" ? BackupDir : OutputEffective;
        if (which != "backup")
        {
            string? created = OutputService.Refresh(Node.Commessa.Id);
            path = created ?? path;
        }

        if (path != null && Directory.Exists(path))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
        }
        else
        {
            Notify.Warning("Apri cartella", "Cartella non raggiungibile: " + path);
        }
    }

    [RelayCommand]
    private void RefreshOutput()
    {
        string? root = OutputService.Refresh(Node.Commessa.Id);
        Main.StatusText = root != null ? "TiaTrackerOut aggiornata: " + root : "TiaTrackerOut non aggiornata: vedere il log";
    }

    public CommessaNode Node { get; }

    public MainViewModel Main { get; }

    public MatrixViewModel Matrix { get; }

    public string Code => Node.Commessa.Code;

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string? Customer { get; set; }

    [ObservableProperty]
    public partial string? Note { get; set; }

    public ObservableCollection<ScanRoot> Roots { get; } = new();

    [ObservableProperty]
    public partial ScanRoot? SelectedRoot { get; set; }

    public ObservableCollection<string> LoadedSummary { get; } = new();

    [ObservableProperty]
    public partial string Stats { get; set; } = "";

    // ---------- modifiche ----------

    public ObservableCollection<CommessaChangeRow> Changes { get; } = new();

    [ObservableProperty]
    public partial CommessaChangeRow? SelectedChange { get; set; }

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowDrafts { get; set; } = true;

    [ObservableProperty]
    public partial bool OnlyNotSaved { get; set; }

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnShowDraftsChanged(bool value) => ApplyFilter();

    partial void OnOnlyNotSavedChanged(bool value) => ApplyFilter();

    public void Refresh()
    {
        Roots.Clear();
        foreach (ScanRoot r in Node.Roots)
        {
            Roots.Add(r);
        }

        List<ProjectVersion> versions = Node.Versions.Select(v => v.Version).ToList();
        Dictionary<long, ProjectVersion> byId = versions.ToDictionary(v => v.Id);
        _allRows = AppServices.Changes.ByCommessa(Node.Commessa.Id).Select(c => new CommessaChangeRow(c, byId)).ToList();
        ApplyFilter();

        LoadedSummary.Clear();
        foreach (VersionLoad l in Node.CurrentLoads.OrderBy(l => l.Plc, StringComparer.OrdinalIgnoreCase))
        {
            string label = byId.TryGetValue(l.VersionId, out ProjectVersion? v) ? v.Label : "?";
            LoadedSummary.Add(l.PlcDisplay + ": " + label + " dal " + l.LoadedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture) +
                              (string.IsNullOrWhiteSpace(l.Note) ? "" : " - " + l.Note));
        }

        if (LoadedSummary.Count == 0)
        {
            LoadedSummary.Add("Nessuna versione segnata come caricata: tasto destro sulla versione > Caricata...");
        }

        int open = Node.Versions.Count(v => v.Status.Open.IsOpen);
        int notSaved = _allRows.Count(r => r.NotSaved);
        int drafts = _allRows.Count(r => r.Draft);
        Stats = (versions.Count == 1 ? "1 versione" : versions.Count + " versioni") +
                $" ({versions.Count(v => v.HasFolder)} con cartella, {open} aperte in TIA) · " +
                $"{_allRows.Count} modifiche, {notSaved} con stati non salvati, {drafts} bozze";
        Matrix.Reload();
        Docs.Invalidate();
        Plan.Invalidate();
        Dashboard?.Invalidate();
        LoadSettings();
    }

    private void ApplyFilter()
    {
        string f = Filter.Trim();
        Changes.Clear();
        foreach (CommessaChangeRow row in _allRows)
        {
            if (!ShowDrafts && row.Draft)
            {
                continue;
            }

            if (OnlyNotSaved && !row.NotSaved)
            {
                continue;
            }

            if (f.Length > 0 &&
                !(row.Title.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                  row.Blocks.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                  (row.Change.Description?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)))
            {
                continue;
            }

            Changes.Add(row);
        }
    }

    /// <summary>Scheda Modifiche con la modifica selezionata (arrivando da una versione); i filtri che la nascondono si tolgono.</summary>
    public void ShowChange(long? changeId)
    {
        SelectedTab = ChangesTab;
        if (changeId == null)
        {
            return;
        }

        if (Changes.All(r => r.Change.Id != changeId))
        {
            Filter = "";
            ShowDrafts = true;
            OnlyNotSaved = false;
        }

        SelectedChange = Changes.FirstOrDefault(r => r.Change.Id == changeId);
    }

    [RelayCommand]
    private void SaveCommessa()
    {
        Node.Commessa.Name = Name;
        Node.Commessa.Customer = Customer;
        Node.Commessa.Note = Note;
        Node.Commessa.OutputDir = OutputDir;
        Node.Commessa.BackupDir = BackupDir;
        AppServices.Commesse.Update(Node.Commessa);
        Node.Commessa = AppServices.Commesse.Get(Node.Commessa.Id)!;
        OutputService.Refresh(Node.Commessa.Id);
        OnPropertyChanged(nameof(OutputEffective));
        Main.StatusText = "Commessa salvata";
    }

    [RelayCommand]
    private void AddRoot()
    {
        OpenFolderDialog dialog = new() { Title = "Cartella con le versioni del progetto TIA" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        ScanRoot root = new() { CommessaId = Node.Commessa.Id, Path = dialog.FolderName };
        AppServices.Commesse.InsertScanRoot(root);
        ScanService.Scan(root);
        Main.Reload(Node);
    }

    /// <summary>
    /// Cambio PC o cartella spostata: i percorsi delle versioni sono relativi
    /// alla radice, basta indicare dove sta adesso.
    /// </summary>
    [RelayCommand]
    private void RelinkRoot(ScanRoot? root)
    {
        root ??= SelectedRoot;
        if (root == null)
        {
            return;
        }

        OpenFolderDialog dialog = new() { Title = "Nuova posizione di " + root.Path };
        if (Directory.Exists(root.Path))
        {
            dialog.InitialDirectory = root.Path;
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string old = root.Path;
        root.Path = dialog.FolderName;
        AppServices.Commesse.UpdateScanRoot(root);
        ScanService.Scan(root);
        Log.Info("Radice riagganciata: " + old + " -> " + root.Path);
        Main.Reload(Node);
    }

    [RelayCommand]
    private void EditRegex(ScanRoot? root)
    {
        root ??= SelectedRoot;
        if (root == null)
        {
            return;
        }

        string? value = TextPromptWindow.Ask(
            "Regex per le etichette delle versioni",
            "Gruppi riconosciuti: maj, min, tia, date, label. Vuoto = regole predefinite\n" +
            "(AUTnnnnnn_Nome_Vx.y-Vnn, AUTnnnnnn_Vnn, Nome_gg-mm-aaaa).",
            root.NameRegex ?? "");
        if (value == null)
        {
            return;
        }

        root.NameRegex = value;
        AppServices.Commesse.UpdateScanRoot(root);
        ScanService.Scan(root);
        Main.Reload(Node);
    }

    /// <summary>Nuova modifica (pulsante o tasto destro sulla commessa): salvata, resta selezionata nella scheda Modifiche.</summary>
    [RelayCommand]
    private void NewChange()
    {
        long? id = ChangeEditing.New(Node.Commessa.Id);
        if (id != null)
        {
            Refresh();
            ShowChange(id);
            Main.StatusText = "Modifica aggiunta a " + Node.Commessa.Code;
        }
    }

    [RelayCommand]
    private void EditChange(CommessaChangeRow? row)
    {
        row ??= SelectedChange;
        if (row != null && ChangeEditing.Edit(AppServices.Changes.Get(row.Change.Id)!))
        {
            Refresh();
        }
    }

    [RelayCommand]
    private void DeleteChange(CommessaChangeRow? row)
    {
        row ??= SelectedChange;
        if (row != null && ChangeEditing.ConfirmDelete(row.Change))
        {
            Refresh();
        }
    }
}
