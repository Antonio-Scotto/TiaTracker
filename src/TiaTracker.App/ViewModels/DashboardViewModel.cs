using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TiaTracker.App.Controls;
using TiaTracker.App.Services;
using TiaTracker.Contracts;
using TiaTracker.Core.Documents;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Planning;
using TiaTracker.Core.Status;
using TiaTracker.Data;
using TiaTracker.Export;

namespace TiaTracker.App.ViewModels;

/// <summary>Una riga "sul PLC" della dashboard.</summary>
public sealed record PlcLoadRow(string Plc, string Version, string When, ChipVm? Proof, string? Note);

/// <summary>Una CPU nella card del progetto TIA.</summary>
public sealed record CpuRow(string Name, string Detail);

/// <summary>Un conteggio (blocchi per tipo, dispositivi...).</summary>
public sealed record CountRow(string Label, string Value);

/// <summary>Una riga delle attivita' recenti.</summary>
public sealed record ActivityRow(string When, string Category, string Title, string? Detail, string Glyph, ChipVm Chip);

/// <summary>
/// Dashboard della commessa dedicata a TIA Portal: cosa c'e' sui PLC, la versione
/// su cui si lavora (salvataggi, snapshot, apertura in TIA), il progetto (CPU,
/// blocchi, hardware, IO), il piano, modifiche e documenti, le attivita' recenti
/// e le azioni piu' usate.
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

    private readonly CommessaNode _node;
    private readonly MainViewModel _main;
    private readonly CommessaDetailViewModel _owner;
    private bool _loaded;
    private bool _dirty = true;
    private bool _loading;

    public DashboardViewModel(CommessaNode node, MainViewModel main, CommessaDetailViewModel owner)
    {
        _node = node;
        _main = main;
        _owner = owner;
    }

    public bool IsShown { get; set; }

    public ObservableCollection<PlcLoadRow> Loads { get; } = new();

    public ObservableCollection<VersionNode> Versions { get; } = new();

    [ObservableProperty]
    public partial VersionNode? WorkNode { get; set; }

    [ObservableProperty]
    public partial string WorkTitle { get; set; } = "";

    public ObservableCollection<ChipVm> WorkChips { get; } = new();

    public ObservableCollection<CountRow> WorkLines { get; } = new();

    [ObservableProperty]
    public partial string TiaTitle { get; set; } = "";

    public ObservableCollection<CpuRow> Cpus { get; } = new();

    public ObservableCollection<CountRow> ProjectCounts { get; } = new();

    public ObservableCollection<CountRow> Blocks { get; } = new();

    [ObservableProperty]
    public partial string? ProjectHint { get; set; }

    [ObservableProperty]
    public partial bool HasPlan { get; set; }

    [ObservableProperty]
    public partial int PlanProgress { get; set; }

    public ObservableCollection<CountRow> PlanLines { get; } = new();

    [ObservableProperty]
    public partial ChipVm? PlanChip { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<GanttBar> Bars { get; set; } = Array.Empty<GanttBar>();

    [ObservableProperty]
    public partial IReadOnlyList<GanttLink> Links { get; set; } = Array.Empty<GanttLink>();

    [ObservableProperty]
    public partial IReadOnlyList<GanttMarker> Markers { get; set; } = Array.Empty<GanttMarker>();

    [ObservableProperty]
    public partial WorkCalendar Calendar { get; set; } = WorkCalendar.Default;

    [ObservableProperty]
    public partial double GanttHeight { get; set; } = 120;

    public ObservableCollection<CountRow> ChangeLines { get; } = new();

    public ObservableCollection<CountRow> DocLines { get; } = new();

    public ObservableCollection<ActivityRow> Activity { get; } = new();

    [ObservableProperty]
    public partial string SnapshotHint { get; set; } = "";

    // ---------- caricamento ----------

    public void EnsureLoaded()
    {
        if (!_loaded || _dirty)
        {
            _ = LoadAsync();
        }
    }

    public void Invalidate()
    {
        _dirty = true;
        if (IsShown && _loaded)
        {
            _ = LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        if (_loading)
        {
            _dirty = true;
            return;
        }

        _loading = true;
        _loaded = true;
        _dirty = false;
        try
        {
            await Build();
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard", ex);
        }
        finally
        {
            _loading = false;
        }

        if (_dirty)
        {
            await LoadAsync();
        }
    }

    private async Task Build()
    {
        long c = _node.Commessa.Id;
        Commessa commessa = _node.Commessa;
        CommessaSettings settings = CommessaSettings.Parse(commessa.SettingsJson);
        WorkCalendar cal = new(settings.WorkOnWeekends);
        Dictionary<long, ProjectVersion> versions = _node.Versions.ToDictionary(v => v.Version.Id, v => v.Version);
        ProjectVersion? work = HardwareService.Reference(commessa);
        Snapshot? snap = work == null ? null : AppServices.Snapshots.Latest(work.Id);
        HwSnapshot? hw = work == null ? null : AppServices.Hardware.Latest(work.Id);
        HardwareExport? data = hw == null ? null : await HardwareService.LoadAsync(hw);
        List<Change> changes = AppServices.Changes.ByCommessa(c);
        List<PlanTask> tasks = AppServices.Plans.Tasks(c);
        ActivityRepository activity = new(AppServices.Db);
        Dictionary<string, int>? kinds = snap == null ? null : activity.BlockKinds(snap.Id);
        CommessaOverview o = CommessaOverview.Build(_node.CurrentLoads, versions, work, snap, kinds, hw, data, changes, tasks, cal, GanttChart.Today);

        // Sul PLC
        Loads.Clear();
        foreach (PlcLoadCard l in o.Loads)
        {
            Loads.Add(new PlcLoadRow(l.Plc, l.VersionLabel, "dal " + Local(l.LoadedUtc, "dd/MM/yyyy HH:mm"),
                l.HasProof ? new ChipVm("snapshot di prova", Tone.Success) : new ChipVm("senza snapshot di prova", Tone.Muted,
                    "Al carico non e' stato fatto lo snapshot dal TIA collegato al PLC"), l.Note));
        }

        // Striscia delle versioni (dalla piu' recente, senza le sparite)
        Versions.Clear();
        foreach (VersionNode v in _node.Versions.Where(v => !v.Version.Missing).Take(12))
        {
            Versions.Add(v);
        }

        // In lavoro
        WorkNode = work == null ? null : _node.Versions.FirstOrDefault(v => v.Version.Id == work.Id);
        WorkChips.Clear();
        WorkLines.Clear();
        if (work == null || WorkNode == null)
        {
            WorkTitle = "Nessuna versione";
            SnapshotHint = "";
        }
        else
        {
            WorkTitle = work.Label + "  ·  " + work.ProjectName;
            foreach (ChipVm chip in WorkNode.Chips)
            {
                WorkChips.Add(chip);
            }

            WorkLines.Add(new CountRow("Ultimo salvataggio", work.LastSavedUtc is DateTime s ? DocumentService.When(s) : "-"));
            WorkLines.Add(new CountRow("Ultimo snapshot", snap == null ? "mai" : DocumentService.When(snap.CreatedUtc) + SourceText(snap.Source)));
            WorkLines.Add(new CountRow("Hardware e rete", hw == null ? "mai letti" : DocumentService.When(hw.CreatedUtc)));
            SnapshotHint = new VersionActions(_main, WorkNode).BestSnapshotSource() is string how ? "Snapshot " + how : "Nessuna fonte per lo snapshot";
        }

        // Progetto TIA
        TiaTitle = o.TiaVersion != null ? "TIA Portal " + o.TiaVersion : "Progetto TIA";
        Cpus.Clear();
        foreach (CpuCard cpu in o.Cpus)
        {
            Cpus.Add(new CpuRow(cpu.Name, string.Join(" · ", new[] { cpu.Type, cpu.Firmware, cpu.Ip, cpu.OrderNumber }.Where(x => !string.IsNullOrEmpty(x)))));
        }

        ProjectCounts.Clear();
        if (hw != null)
        {
            ProjectCounts.Add(new CountRow("Dispositivi", hw.DeviceCount.ToString(It)));
            ProjectCounts.Add(new CountRow("Moduli", hw.ModuleCount.ToString(It)));
            ProjectCounts.Add(new CountRow("Segnali I/O", hw.IoTagCount.ToString(It)));
            ProjectCounts.Add(new CountRow("Indirizzi IP", hw.IpCount.ToString(It)));
        }

        Blocks.Clear();
        foreach ((string label, int count) in o.Blocks)
        {
            Blocks.Add(new CountRow(label, count.ToString(It)));
        }

        if (o.Blocks.Count > 0)
        {
            Blocks.Add(new CountRow("Totale", o.BlocksTotal.ToString(It)));
        }

        ProjectHint = hw == null && snap == null
            ? "Nessun export da TIA della versione di lavoro: \"Snapshot da TIA\" legge blocchi, hardware, rete e tag."
            : hw == null ? "Hardware non ancora letto: \"Leggi hardware e rete\" dal menu della versione." : null;

        // Pianificazione
        HasPlan = o.Plan != null;
        PlanLines.Clear();
        PlanChip = null;
        if (o.Plan is PlanKpi k)
        {
            PlanProgress = k.ProgressPercent;
            PlanLines.Add(new CountRow("Attivita'", k.Done + " fatte su " + k.Tasks + (k.InProgress > 0 ? ", " + k.InProgress + " in corso" : "")));
            if (k.NextMilestone != null)
            {
                PlanLines.Add(new CountRow("Prossima milestone", k.NextMilestone.Title + " · " + k.NextMilestone.End.ToString("ddd dd/MM", It)));
            }

            if (k.PlanEnd != null)
            {
                PlanLines.Add(new CountRow("Fine prevista", k.PlanEnd.Value.ToString("dd/MM/yyyy", It)));
            }

            if (k.EstimatedHours > 0 || k.ActualHours > 0)
            {
                PlanLines.Add(new CountRow("Ore", k.ActualHours.ToString("0.#", It) + " su " + k.EstimatedHours.ToString("0.#", It) + " stimate"));
            }

            PlanChip = k.Late > 0 ? new ChipVm(k.Late + " in ritardo", Tone.Danger)
                : k.Blocked > 0 ? new ChipVm(k.Blocked + " bloccate", Tone.Warning)
                : new ChipVm("In linea", Tone.Success);
            BuildGantt(tasks, cal);
        }
        else
        {
            Bars = Array.Empty<GanttBar>();
            Links = Array.Empty<GanttLink>();
        }

        Calendar = cal;

        // Modifiche e documenti
        ChangeLines.Clear();
        ChangeLines.Add(new CountRow("Registrate", o.Changes.ToString(It)));
        if (work != null)
        {
            ChangeLines.Add(new CountRow("Da salvare su " + work.Label, o.ChangesOpenOnWork.ToString(It)));
        }

        if (o.Drafts > 0)
        {
            ChangeLines.Add(new CountRow("Bozze da confermare", o.Drafts.ToString(It)));
        }

        DocLines.Clear();
        Dictionary<string, DocExport> exports = AppServices.Docs.LatestExports(c);
        foreach (string kind in DocumentService.Kinds.Where(k => k != DocKinds.Plan || tasks.Count > 0))
        {
            DocLines.Add(new CountRow(DocKinds.Title(kind), exports.TryGetValue(kind, out DocExport? e)
                ? DocumentService.When(e.GeneratedUtc) + (kind is DocKinds.Hardware or DocKinds.Io && hw != null && e.HwSnapshotId != hw.Id ? " · da rigenerare" : "")
                : "mai generato"));
        }

        // Attivita' recenti
        Activity.Clear();
        foreach (ActivityItem a in activity.Recent(c, 30))
        {
            (string glyph, Tone tone, string label) = a.Category switch
            {
                "versione" => ("\uE8B7", Tone.Accent, "Versione"),
                "modifica" => ("\uE70B", Tone.Purple, "Modifica"),
                "piano" => ("\uE787", Tone.Orange, "Piano"),
                "snapshot" => ("\uE722", Tone.Teal, "Snapshot"),
                "hardware" => ("\uE950", Tone.Teal, "Hardware"),
                "documenti" => ("\uE8A5", Tone.Neutral, "Documenti"),
                _ => ("\uE946", Tone.Neutral, a.Category),
            };
            Activity.Add(new ActivityRow(Local(a.Utc, "dd/MM HH:mm"), a.Category, a.Title, a.Detail, glyph, new ChipVm(label, tone)));
        }
    }

    private void BuildGantt(List<PlanTask> tasks, WorkCalendar cal)
    {
        // Sola lettura: le attivita' aperte o che toccano le prossime 6 settimane, al massimo 10 righe.
        List<PlanTask> all = tasks.Select(t => t.Clone()).ToList();
        PlanRollup.Apply(all, cal);
        DateOnly today = GanttChart.Today, horizon = today.AddDays(42);
        List<PlanTask> shown = all.Where(t => !t.IsPhase && t.Status != PlanStatus.Cancelled &&
                                              ((!t.IsClosed && t.Start <= horizon) || (t.End >= today.AddDays(-7) && t.Start <= horizon)))
            .OrderBy(t => t.Start).ThenBy(t => t.Sort).Take(10).ToList();
        List<GanttBar> bars = new();
        for (int i = 0; i < shown.Count; i++)
        {
            PlanTask t = shown[i];
            bars.Add(new GanttBar
            {
                Id = t.Id,
                Row = i,
                Start = t.Start,
                End = t.IsMilestone ? t.Start : t.End,
                Kind = t.Kind,
                Progress = t.Status == PlanStatus.Done ? 100 : t.Progress,
                Tone = t.IsMilestone ? Tone.Orange : PlanStates.ToneOf(t.Status) == Tone.Neutral ? Tone.Accent : PlanStates.ToneOf(t.Status),
                Title = t.Title + (t.Assignee != null ? "  · " + t.Assignee : ""),
                Late = t.IsLate(today),
                Draggable = false,
            });
        }

        HashSet<long> ids = shown.Select(t => t.Id).ToHashSet();
        Bars = bars;
        Links = AppServices.Plans.Links(_node.Commessa.Id).Where(l => ids.Contains(l.PredecessorId) && ids.Contains(l.SuccessorId))
            .Select(l => new GanttLink(l.Id, l.PredecessorId, l.SuccessorId, false)).ToList();
        GanttHeight = GanttChart.HeaderHeight + Math.Max(2, bars.Count) * GanttChart.RowHeight + 4;
        Markers = _node.CurrentLoads.Select(l => new GanttMarker(DateOnly.FromDateTime(DateTime.SpecifyKind(l.LoadedUtc, DateTimeKind.Utc).ToLocalTime()),
            (_node.Versions.FirstOrDefault(v => v.Version.Id == l.VersionId)?.Label ?? "?") + " → " + l.PlcDisplay, Tone.Success)).ToList();
    }

    private static string SourceText(string source) => source switch
    {
        SnapshotSources.Attach => " (TIA aperto)",
        SnapshotSources.File => " (cartella)",
        SnapshotSources.Backup => " (backup)",
        SnapshotSources.Rar => " (rar)",
        _ => "",
    };

    private static string Local(DateTime utc, string format) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString(format, It);

    // ---------- azioni rapide ----------

    [RelayCommand]
    private async Task Snapshot()
    {
        if (WorkNode == null)
        {
            Notify.Info("Snapshot", "La commessa non ha una versione di lavoro.");
            return;
        }

        await new VersionActions(_main, WorkNode).SnapshotBest("Snapshot " + WorkNode.Label);
        Invalidate();
    }

    [RelayCommand]
    private async Task ReadHardware()
    {
        if (WorkNode != null)
        {
            await new VersionActions(_main, WorkNode).ReadHardware();
            Invalidate();
        }
    }

    [RelayCommand]
    private async Task NewVersion()
    {
        if (WorkNode != null)
        {
            await new VersionActions(_main, WorkNode).NewVersionFrom();
            Invalidate();
        }
    }

    [RelayCommand]
    private async Task GenerateDocuments()
    {
        await DocumentService.ExportAsync(_node.Commessa.Id, null, DocumentService.Kinds.ToArray(), DocExporter.All.ToArray());
        Invalidate();
        _owner.Docs.Invalidate();
    }

    [RelayCommand]
    private void NewTask()
    {
        _owner.SelectedTab = CommessaDetailViewModel.PlanningTab;
        _owner.Plan.EnsureLoaded();
        _owner.Plan.NewTaskCommand.Execute(null);
    }

    [RelayCommand]
    private void OpenOutput()
    {
        string? root = OutputService.Refresh(_node.Commessa.Id);
        if (root == null)
        {
            Notify.Warning("TiaTrackerOut", "La commessa non ha una cartella TiaTrackerOut raggiungibile.");
            return;
        }

        DocumentService.OpenFolder(root);
    }

    [RelayCommand]
    private void OpenTab(string? tab)
    {
        _owner.SelectedTab = tab switch
        {
            "modifiche" => CommessaDetailViewModel.ChangesTab,
            "documenti" => CommessaDetailViewModel.DocumentsTab,
            "pianificazione" => CommessaDetailViewModel.PlanningTab,
            "rete" => CommessaDetailViewModel.NetworkTab,
            "impostazioni" => CommessaDetailViewModel.SettingsTab,
            _ => _owner.SelectedTab,
        };
    }

    [RelayCommand]
    private void OpenVersion(VersionNode? node)
    {
        if (node != null)
        {
            node.IsSelected = true;
        }
    }

    public MainViewModel Main => _main;
}
