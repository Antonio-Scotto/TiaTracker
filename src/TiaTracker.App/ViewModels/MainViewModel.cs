using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TiaTracker.App.Services;
using TiaTracker.App.Views;
using TiaTracker.Core.Domain;
using TiaTracker.Data;

namespace TiaTracker.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _openTimer;
    private readonly DispatcherTimer _scanTimer;
    private DateTime _lastAutoScanUtc = DateTime.MinValue;
    private bool _autoScanning;

    public MainViewModel()
    {
        // Lo stato "aperta in TIA" cambia senza che l'app lo sappia: si rilegge
        // il file system ogni 15 s, senza toccare il worker.
        _openTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _openTimer.Tick += (_, _) => RefreshOpenStates();

        // Versioni nuove o salvate in TIA: scansione silenziosa ogni N minuti (impostazione).
        _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(Math.Max(1, AppServices.Settings.AutoScanMinutes)) };
        _scanTimer.Tick += (_, _) => _ = AutoScanAsync();
        JobRunner.Attach(Job, s => StatusText = s);
    }

    public ObservableCollection<CommessaNode> Commesse { get; } = new();

    [ObservableProperty]
    public partial object? SelectedNode { get; set; }

    [ObservableProperty]
    public partial object? Detail { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    public partial bool IsBusy { get; set; }

    public string DataInfo => "Dati: " + DataPaths.Root + " (" + DataPaths.Mode + ")";

    /// <summary>Barra in alto: solo il tipo di archivio, il percorso e' nel suggerimento.</summary>
    public string DataShort => "Archivio: " + DataPaths.Mode;

    public string CommesseCount => Commesse.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Filtro dell'albero: codice/nome della commessa, etichetta o nome progetto della versione.</summary>
    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    /// <summary>Mostra anche vecchie, archiviate e scartate (ricordato fra le sessioni).</summary>
    [ObservableProperty]
    public partial bool ShowRetired { get; set; } = AppServices.Settings.ShowRetiredVersions;

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnShowRetiredChanged(bool value)
    {
        AppServices.Settings.ShowRetiredVersions = value;
        ApplyFilter();
    }

    public void ApplyFilter()
    {
        string f = Filter.Trim();
        foreach (CommessaNode c in Commesse)
        {
            bool commessaMatch = f.Length == 0 ||
                                 c.Commessa.Code.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                                 c.Commessa.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                                 (c.Commessa.Customer?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false);
            bool anyVersion = false;
            foreach (VersionNode v in c.Versions)
            {
                bool textOk = commessaMatch ||
                              v.Label.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                              v.Version.ProjectName.Contains(f, StringComparison.OrdinalIgnoreCase);
                // La versione selezionata resta visibile anche se il filtro la nasconderebbe.
                v.IsVisible = v.IsSelected || (textOk && (ShowRetired || !v.IsRetired));
                anyVersion |= textOk && f.Length > 0;
            }

            c.IsVisible = commessaMatch || anyVersion;
        }
    }

    /// <summary>Il job worker in corso, uno alla volta in tutta l'app.</summary>
    public JobState Job { get; } = new();

    public void Load()
    {
        bool tia = ScanService.IsTiaRunning();
        Commesse.Clear();
        foreach (Commessa c in AppServices.Commesse.All())
        {
            CommessaNode node = new(c);
            node.Reload(tia);
            Commesse.Add(node);
        }

        OnPropertyChanged(nameof(CommesseCount));
        ApplyFilter();

        // Selezione dell'ultima sessione.
        VersionNode? lastVersion = Commesse.SelectMany(c => c.Versions)
            .FirstOrDefault(v => v.Version.Id == AppServices.Settings.LastVersionId);
        if (lastVersion != null)
        {
            lastVersion.IsSelected = true;
        }
        else
        {
            CommessaNode? last = Commesse.FirstOrDefault(c => c.Commessa.Id == AppServices.Settings.LastCommessaId) ?? Commesse.FirstOrDefault();
            if (last != null)
            {
                last.IsSelected = true;
            }
        }

        StatusText = Commesse.Count == 0
            ? "Nessuna commessa: crearne una con \"Nuova commessa\"."
            : Commesse.Count + " commesse, " + Commesse.Sum(c => c.Versions.Count) + " versioni";
        _openTimer.Start();
        CheckWorkers();
        if (AppServices.Settings.AutoScanMinutes > 0)
        {
            _scanTimer.Start();
            _ = AutoScanAsync();
        }
    }

    /// <summary>Dopo un cambio dell'impostazione: timer con il nuovo intervallo, o fermo se 0.</summary>
    public void ApplyAutoScanSetting()
    {
        _scanTimer.Stop();
        if (AppServices.Settings.AutoScanMinutes > 0)
        {
            _scanTimer.Interval = TimeSpan.FromMinutes(AppServices.Settings.AutoScanMinutes);
            _scanTimer.Start();
        }
    }

    /// <summary>Ritorno alla finestra (es. dopo un salvataggio in TIA): scansione se l'ultima e' di qualche minuto fa.</summary>
    public void OnActivated()
    {
        if (AppServices.Settings.AutoScanMinutes > 0 && DateTime.UtcNow - _lastAutoScanUtc > TimeSpan.FromMinutes(2))
        {
            _ = AutoScanAsync();
        }
    }

    /// <summary>
    /// Scansione silenziosa di tutte le commesse: disco in background, DB sul thread UI,
    /// albero ricaricato solo se qualcosa e' cambiato, notifica per ogni versione nuova.
    /// Cartelle non raggiungibili (rete giu') si saltano senza segnare versioni sparite.
    /// </summary>
    public async Task AutoScanAsync()
    {
        if (_autoScanning || IsBusy)
        {
            return;
        }

        _autoScanning = true;
        _lastAutoScanUtc = DateTime.UtcNow;
        try
        {
            foreach (CommessaNode node in Commesse.ToList())
            {
                HashSet<long> before = node.Versions.Select(v => v.Version.Id).ToHashSet();
                bool changed = false;
                foreach (ScanRoot root in AppServices.Commesse.ScanRoots(node.Commessa.Id))
                {
                    List<Core.Scanning.ScannedVersion> found;
                    try
                    {
                        found = await Task.Run(() => ScanService.ScanFolder(root));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }

                    if (IsBusy)
                    {
                        return; // e' partita una scansione a mano: vale quella
                    }

                    ScanSummary s = ScanService.Apply(root, found);
                    changed |= s.New > 0 || s.Updated > 0 || s.Missing > 0;
                }

                if (!changed)
                {
                    continue;
                }

                Reload(node);
                foreach (VersionNode v in node.Versions.Where(v => !before.Contains(v.Version.Id) && !v.Version.Missing))
                {
                    NotifyNewVersion(node, v);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Scansione automatica", ex);
        }
        finally
        {
            _autoScanning = false;
        }
    }

    private void NotifyNewVersion(CommessaNode node, VersionNode v)
    {
        List<string> previous = node.Versions
            .Where(x => x != v && x.Version.State == VersionState.InLavoro && !x.Status.OnPlc)
            .Select(x => x.Version.Label).ToList();
        string message = v.Version.ProjectName + " trovata nella cartella delle versioni." +
                         (previous.Count > 0 ? " Con \"Imposta In lavoro\" " + string.Join(", ", previous) + " diventa Vecchia." : "");
        Notify.Info("Nuova versione " + v.Version.Label + " · " + node.Commessa.Code, message,
            new NoticeAction("Imposta In lavoro", () => _ = new VersionActions(this, v).SetAsWorkVersion()),
            new NoticeAction("Mostra", () => v.IsSelected = true));
    }

    /// <summary>Pallino rosso sul pulsante Diagnostica: manca un worker.</summary>
    [ObservableProperty]
    public partial bool DiagnosticsProblem { get; set; }

    /// <summary>Solo file system: dice subito se manca un worker, non al primo snapshot.</summary>
    public void CheckWorkers() => DiagnosticsProblem = !WorkerHealth.CheckAndNotify(Views.DiagnosticsWindow.ShowSingle);

    [RelayCommand]
    private void OpenDiagnostics()
    {
        Views.DiagnosticsWindow.ShowSingle();
        CheckWorkers();
    }

    partial void OnSelectedNodeChanged(object? value)
    {
        Detail = value switch
        {
            CommessaNode c => new CommessaDetailViewModel(c, this),
            VersionNode v => new VersionDetailViewModel(v, this),
            _ => null,
        };

        if (value is VersionNode vn)
        {
            AppServices.Settings.LastVersionId = vn.Version.Id;
            AppServices.Settings.LastCommessaId = vn.Parent.Commessa.Id;
        }
        else if (value is CommessaNode cn)
        {
            AppServices.Settings.LastVersionId = null;
            AppServices.Settings.LastCommessaId = cn.Commessa.Id;
        }
    }

    public CommessaNode? CurrentCommessa => SelectedNode switch
    {
        CommessaNode c => c,
        VersionNode v => v.Parent,
        _ => null,
    };

    /// <summary>Seleziona la commessa e ne restituisce il dettaglio (tasto destro sull'albero).</summary>
    public CommessaDetailViewModel? ShowCommessa(CommessaNode node)
    {
        node.IsSelected = true;
        return Detail is CommessaDetailViewModel cd && cd.Node == node ? cd : null;
    }

    /// <summary>Ricarica una commessa (versioni e stati) dopo una modifica.</summary>
    public void Reload(CommessaNode node)
    {
        node.Reload(ScanService.IsTiaRunning());
        ApplyFilter();
        if (Detail is VersionDetailViewModel vd && vd.Node.Parent == node)
        {
            vd.Refresh();
        }
        else if (Detail is CommessaDetailViewModel cd && cd.Node == node)
        {
            cd.Refresh();
        }
    }

    private void RefreshOpenStates()
    {
        if (IsBusy)
        {
            return;
        }

        bool tia = ScanService.IsTiaRunning();
        foreach (CommessaNode node in Commesse)
        {
            node.RefreshOpenStates(tia);
        }
    }

    private bool CanScan() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task Scan()
    {
        List<CommessaNode> targets = CurrentCommessa != null ? new() { CurrentCommessa } : Commesse.ToList();
        IsBusy = true;
        StatusText = "Scansione in corso...";
        try
        {
            List<string> report = new();
            foreach (CommessaNode node in targets)
            {
                foreach (ScanRoot root in AppServices.Commesse.ScanRoots(node.Commessa.Id))
                {
                    try
                    {
                        ScanSummary s = await ScanService.ScanAsync(root);
                        report.Add($"{node.Commessa.Code}: {s.Found} versioni ({s.New} nuove, {s.Updated} aggiornate, {s.Missing} sparite)");
                    }
                    catch (DirectoryNotFoundException)
                    {
                        report.Add($"{node.Commessa.Code}: cartella non trovata {root.Path} - usare \"Riaggancia cartella\"");
                    }
                }

                Reload(node);
            }

            StatusText = string.Join("   ", report);
        }
        catch (Exception ex)
        {
            Log.Error("Scansione fallita", ex);
            StatusText = "Scansione fallita: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Nuova commessa: versioni (gia' lette dal dialogo), versione su cui si lavora In
    /// lavoro, TiaTrackerOut, poi un solo lavoro di export da TIA (blocchi, hardware,
    /// rete e tag) dalla fonte migliore: Lista IP allineata e documenti in Elenchi.
    /// </summary>
    [RelayCommand]
    private async Task NewCommessa()
    {
        NewCommessaViewModel vm = new();
        NewCommessaWindow dialog = new(vm) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        CommessaSettings settings = new() { AutoDocuments = vm.AutoDocuments };
        Commessa c = new()
        {
            Code = vm.Code.Trim(),
            Name = vm.CommessaName.Trim(),
            Customer = Blank(vm.Customer),
            OutputDir = Blank(vm.OutputDir),
            BackupDir = Blank(vm.BackupDir),
            SettingsJson = settings.ToJson(),
        };
        try
        {
            AppServices.Commesse.Insert(c);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            Notify.Error("Commessa non creata", "Esiste gia' una commessa con codice " + c.Code + ".");
            return;
        }

        ScanRoot root = new() { CommessaId = c.Id, Path = vm.Folder.Trim(), NameRegex = Blank(vm.NameRegex) };
        AppServices.Commesse.InsertScanRoot(root);
        ScanSummary s = vm.Scanned != null ? ScanService.Apply(root, vm.Scanned) : await ScanService.ScanAsync(root);

        CommessaNode node = new(c);
        node.Reload(ScanService.IsTiaRunning());
        Commesse.Add(node);
        OnPropertyChanged(nameof(CommesseCount));
        ApplyFilter();
        node.IsSelected = true;

        VersionNode? work = vm.WorkVersion == null
            ? null
            : node.Versions.FirstOrDefault(v => v.Version.ProjectName.Equals(vm.WorkVersion.ProjectName, StringComparison.OrdinalIgnoreCase));
        if (work != null && vm.SetInLavoro)
        {
            await new VersionActions(this, work).SetState(VersionState.InLavoro);
        }

        string? output = OutputService.Refresh(c.Id);
        string foundText = s.Found == 1 ? "1 versione trovata" : s.Found + " versioni trovate";
        StatusText = c.Code + ": " + foundText;
        Log.Info("Nuova commessa " + c.Code + " in " + root.Path + ": " + s.Found + " versioni, TiaTrackerOut " + (output ?? "-"));
        NoticeAction[] open = output == null
            ? Array.Empty<NoticeAction>()
            : new[] { new NoticeAction("Apri TiaTrackerOut", () => DocumentService.OpenFolder(output)) };
        if (work == null || !vm.InitialExport || !vm.CanExport)
        {
            Notify.Success("Commessa " + c.Display + " creata", foundText +
                                                                 (work != null && vm.SetInLavoro ? ", " + work.Version.Label + " in lavoro" : "") + ".", open);
            return;
        }

        // Un solo lavoro: lo snapshot legge anche hardware, rete e tag; se la versione e' quella di
        // riferimento la Lista IP si allinea e i documenti si generano da soli.
        SnapshotService.Outcome? r = await new VersionActions(this, work).SnapshotBest("Export iniziale " + c.Code + " (" + work.Version.Label + ")");
        if (r is { Ok: true } && vm.AutoDocuments && AppServices.Docs.LatestExports(c.Id).Count == 0)
        {
            await DocumentService.ExportAsync(c.Id, work.Version.Id, DocumentService.Kinds.ToArray(), Export.DocExporter.All.ToArray());
        }
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    [RelayCommand]
    private void ImportHistory()
    {
        CommessaNode? node = CurrentCommessa;
        if (node == null)
        {
            Notify.Info("Importa storico", "Selezionare prima una commessa.");
            return;
        }

        ImportWindow window = new() { Owner = Application.Current.MainWindow, DataContext = new ImportViewModel(node, this) };
        window.ShowDialog();
        Reload(node);
    }
}

/// <summary>Stato del job worker corrente, mostrato nella barra in basso.</summary>
public sealed partial class JobState : ObservableObject
{
    [ObservableProperty]
    public partial bool Running { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool Indeterminate { get; set; } = true;

    public Action? CancelAction { get; set; }

    [RelayCommand]
    private void Cancel() => CancelAction?.Invoke();
}
