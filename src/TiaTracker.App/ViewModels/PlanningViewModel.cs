using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TiaTracker.App.Controls;
using TiaTracker.App.Services;
using TiaTracker.App.Views;
using TiaTracker.Core.Documents;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Planning;
using TiaTracker.Data;
using TiaTracker.Export;

namespace TiaTracker.App.ViewModels;

/// <summary>Una riga della griglia della pianificazione (stessa riga del Gantt).</summary>
public sealed partial class PlanRowVm : ObservableObject
{
    private static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");
    private static readonly string Collapsed = char.ConvertFromUtf32(0xE76C);
    private static readonly string Expanded = char.ConvertFromUtf32(0xE70D);
    private static readonly string Flag = char.ConvertFromUtf32(0xE7C1);

    public PlanRowVm(PlanTask task, int level, bool hasChildren, bool expanded, bool late, string? versionLabel, string predecessors, int changes,
        WorkCalendar cal)
    {
        Task = task;
        Level = level;
        HasChildren = hasChildren;
        IsExpanded = expanded;
        IsLate = late;
        VersionLabel = versionLabel;
        PredecessorsText = predecessors;
        ChangesCount = changes;
        DurationText = task.IsMilestone ? "" : cal.Duration(task.Start, task.End) + " gg";
    }

    public PlanTask Task { get; }

    public long Id => Task.Id;

    public int Level { get; }

    public Thickness Indent => new(Level * 18, 0, 0, 0);

    public bool HasChildren { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph))]
    public partial bool IsExpanded { get; set; }

    public string Glyph => Task.IsPhase ? (IsExpanded ? Expanded : Collapsed) : Task.IsMilestone ? Flag : "";

    public string Title => Task.Title;

    public bool IsPhase => Task.IsPhase;

    public bool IsLate { get; }

    public string StartText => Task.Start.ToString("ddd dd/MM", It);

    public string EndText => Task.IsMilestone ? "" : Task.End.ToString("ddd dd/MM", It);

    public string DurationText { get; }

    public string ProgressText => Task.IsMilestone ? "" : Task.Progress + "%";

    public string? Assignee => Task.Assignee;

    public ChipVm StatusChip => IsLate
        ? new ChipVm("In ritardo", Tone.Danger, PlanStates.Italian(Task.Status) + ", doveva finire il " + Task.End.ToString("dd/MM", It))
        : new ChipVm(PlanStates.Italian(Task.Status), PlanStates.ToneOf(Task.Status));

    public string? VersionLabel { get; }

    public string PredecessorsText { get; }

    public int ChangesCount { get; }

    public string ToolTip
    {
        get
        {
            List<string> lines = new() { Task.Title };
            lines.Add(Task.IsMilestone ? Task.Start.ToString("dddd dd/MM/yyyy", It)
                : Task.Start.ToString("ddd dd/MM", It) + " → " + Task.End.ToString("ddd dd/MM", It) + " (" + DurationText + ")");
            lines.Add(PlanStates.Italian(Task.Status) + (Task.IsMilestone ? "" : " · " + Task.Progress + "%") +
                      (Task.Assignee != null ? " · " + Task.Assignee : ""));
            if (PredecessorsText.Length > 0)
            {
                lines.Add("Dopo: " + PredecessorsText);
            }

            if (VersionLabel != null)
            {
                lines.Add("Versione " + VersionLabel + (ChangesCount > 0 ? " · " + ChangesCount + " modifiche collegate" : ""));
            }

            if (Task.Locked)
            {
                lines.Add("Data fissata");
            }

            return string.Join("\n", lines);
        }
    }
}

/// <summary>Una riga dello storico delle tempistiche.</summary>
public sealed class PlanEventRow
{
    private static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

    public PlanEventRow(PlanEvent e)
    {
        Event = e;
    }

    public PlanEvent Event { get; }

    public string When => Local(Event.Utc);

    public string? User => Event.User;

    public string Task => Event.TaskTitle;

    public string What => Event.Kind switch
    {
        PlanEventKinds.Created => "Creato",
        PlanEventKinds.Deleted => "Eliminato",
        PlanEventKinds.Linked => "Collegato",
        PlanEventKinds.Unlinked => "Scollegato",
        PlanEventKinds.Undone => "Annullato · " + PlanFields.Label(Event.Field),
        _ => PlanFields.Label(Event.Field),
    };

    public string? From => PlanFields.Display(Event.Field, Event.OldValue);

    public string? To => PlanFields.Display(Event.Field, Event.NewValue);

    public string Shift => Event.ShiftText.Length == 0 ? "" : Event.ShiftText + " gg";

    public string? Reason => Event.Reason;

    public string Origin => Event.Cascade ? "cascata" : Event.UndoneBy != null ? "annullato dopo" : "";

    public bool IsUndone => Event.UndoneBy != null;

    private static string Local(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM/yy HH:mm", It);
}

/// <summary>
/// Scheda Pianificazione: attivita', milestone e fasi della commessa con dipendenze
/// fine-inizio, Gantt, cascata delle date con motivo e annullamento, storico delle
/// tempistiche, legame con versioni e modifiche del registro.
/// </summary>
public sealed partial class PlanningViewModel : ObservableObject
{
    public const string AllAssignees = "Tutti";

    private readonly CommessaNode _node;
    private readonly MainViewModel _main;
    private readonly HashSet<long> _collapsed = new();
    private List<PlanTask> _tasks = new();
    private List<PlanLink> _links = new();
    private bool _loaded;
    private bool _dirty = true;

    /// <summary>Durante il ricaricamento le tendine dei filtri cambiano: niente ricostruzioni intermedie.</summary>
    private bool _suspend;

    public PlanningViewModel(CommessaNode node, MainViewModel main)
    {
        _node = node;
        _main = main;
        StatusFilters = new[] { "Tutti", "Aperti", "In ritardo", "Da fare", "In corso", "Bloccati", "Fatti" };
    }

    public long CommessaId => _node.Commessa.Id;

    public bool IsShown { get; set; }

    public static string User => Environment.UserName;

    public WorkCalendar Calendar { get; private set; } = WorkCalendar.Default;

    public ObservableCollection<PlanRowVm> Rows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedId), nameof(HasSelection))]
    public partial PlanRowVm? Selected { get; set; }

    public long? SelectedId => Selected?.Id;

    public bool HasSelection => Selected != null;

    [ObservableProperty]
    public partial IReadOnlyList<GanttBar> Bars { get; set; } = Array.Empty<GanttBar>();

    [ObservableProperty]
    public partial IReadOnlyList<GanttLink> Links { get; set; } = Array.Empty<GanttLink>();

    [ObservableProperty]
    public partial IReadOnlyList<GanttMarker> Markers { get; set; } = Array.Empty<GanttMarker>();

    [ObservableProperty]
    public partial string KpiText { get; set; } = "";

    [ObservableProperty]
    public partial ChipVm? LateChip { get; set; }

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    public IReadOnlyList<string> StatusFilters { get; }

    [ObservableProperty]
    public partial string StatusFilter { get; set; } = "Tutti";

    public ObservableCollection<string> Assignees { get; } = new() { AllAssignees };

    [ObservableProperty]
    public partial string AssigneeFilter { get; set; } = AllAssignees;

    [ObservableProperty]
    public partial bool ShowHistory { get; set; }

    [ObservableProperty]
    public partial bool OnlyDateHistory { get; set; } = true;

    public ObservableCollection<PlanEventRow> History { get; } = new();

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    partial void OnFilterChanged(string value)
    {
        if (!_suspend)
        {
            Rebuild();
        }
    }

    partial void OnStatusFilterChanged(string value)
    {
        if (!_suspend)
        {
            Rebuild();
        }
    }

    partial void OnAssigneeFilterChanged(string value)
    {
        // La tendina, svuotata, rimanda null: vale "Tutti".
        if (value == null)
        {
            AssigneeFilter = AllAssignees;
            return;
        }

        if (!_suspend)
        {
            Rebuild();
        }
    }

    partial void OnShowHistoryChanged(bool value) => LoadHistory();

    partial void OnOnlyDateHistoryChanged(bool value) => LoadHistory();

    // ---------- caricamento ----------

    public void EnsureLoaded()
    {
        if (!_loaded || _dirty)
        {
            Reload();
        }
    }

    public void Invalidate()
    {
        _dirty = true;
        if (IsShown && _loaded)
        {
            Reload();
        }
    }

    public void Reload()
    {
        long? keepSelected = Selected?.Id;
        _loaded = true;
        _dirty = false;
        CommessaSettings settings = CommessaSettings.Parse(_node.Commessa.SettingsJson);
        Calendar = new WorkCalendar(settings.WorkOnWeekends);
        _tasks = AppServices.Plans.Tasks(CommessaId);
        _links = AppServices.Plans.Links(CommessaId);
        PlanRollup.Apply(_tasks, Calendar);

        string keep = AssigneeFilter;
        _suspend = true;
        try
        {
            List<string> names = AppServices.Plans.Assignees(CommessaId);
            foreach (string gone in Assignees.Skip(1).Where(a => !names.Contains(a)).ToList())
            {
                Assignees.Remove(gone);
            }

            foreach (string a in names.Where(a => !Assignees.Contains(a)))
            {
                Assignees.Add(a);
            }

            AssigneeFilter = Assignees.Contains(keep) ? keep : AllAssignees;
        }
        finally
        {
            _suspend = false;
        }

        Markers = BuildMarkers();
        Rebuild(keepSelected);
        UpdateKpi();
        LoadHistory();
        OnPropertyChanged(nameof(Calendar));
    }

    private IReadOnlyList<GanttMarker> BuildMarkers()
    {
        List<GanttMarker> markers = new();
        Dictionary<long, ProjectVersion> versions = _node.Versions.Select(v => v.Version).ToDictionary(v => v.Id);
        foreach (VersionLoad l in AppServices.Versions.Loads(CommessaId))
        {
            if (versions.TryGetValue(l.VersionId, out ProjectVersion? v))
            {
                DateTime local = DateTime.SpecifyKind(l.LoadedUtc, DateTimeKind.Utc).ToLocalTime();
                markers.Add(new GanttMarker(DateOnly.FromDateTime(local), v.Label + " → " + l.PlcDisplay, l.IsCurrent ? Tone.Success : Tone.Neutral,
                    v.Label + " caricata su " + l.PlcDisplay + " il " + local.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) +
                    (l.IsCurrent ? "" : " (non piu' sul PLC)")));
            }
        }

        // Versioni nate dopo la creazione della commessa (le prime trovate hanno tutte la stessa data).
        DateTime created = _node.Commessa.CreatedUtc.AddDays(1);
        foreach (ProjectVersion v in versions.Values.Where(v => v.FirstSeenUtc > created && !v.Missing))
        {
            DateTime local = DateTime.SpecifyKind(v.FirstSeenUtc, DateTimeKind.Utc).ToLocalTime();
            markers.Add(new GanttMarker(DateOnly.FromDateTime(local), v.Label, Tone.Accent, "Nuova versione " + v.Label + " (" + v.ProjectName + ")"));
        }

        return markers;
    }

    /// <summary>Righe visibili: albero delle fasi (chiuse/aperte) o, con un filtro, i task che passano con le loro fasi.</summary>
    private void Rebuild(long? keepSelected = null)
    {
        long? keep = keepSelected ?? Selected?.Id;
        DateOnly today = GanttChart.Today;
        Dictionary<long, PlanTask> byId = _tasks.ToDictionary(t => t.Id);
        Dictionary<long, List<PlanTask>> children = _tasks.Where(t => t.ParentId != null && byId.ContainsKey(t.ParentId.Value))
            .GroupBy(t => t.ParentId!.Value).ToDictionary(g => g.Key, g => g.OrderBy(t => t.Sort).ThenBy(t => t.Id).ToList());
        List<PlanTask> roots = _tasks.Where(t => t.ParentId == null || !byId.ContainsKey(t.ParentId.Value)).OrderBy(t => t.Sort).ThenBy(t => t.Id).ToList();
        Dictionary<long, string> versionLabels = _node.Versions.ToDictionary(v => v.Version.Id, v => v.Version.Label);
        Dictionary<long, List<long>> changeLinks = AppServices.Plans.ChangeLinks(CommessaId);
        ILookup<long, PlanLink> preds = _links.ToLookup(l => l.SuccessorId);

        bool filtering = Filter.Trim().Length > 0 || StatusFilter != "Tutti" || AssigneeFilter != AllAssignees;
        HashSet<long> visibleByFilter = new();
        if (filtering)
        {
            foreach (PlanTask t in _tasks.Where(t => !t.IsPhase && Matches(t, today)))
            {
                visibleByFilter.Add(t.Id);
                for (long? p = t.ParentId; p != null && byId.TryGetValue(p.Value, out PlanTask? phase); p = phase.ParentId)
                {
                    if (!visibleByFilter.Add(phase.Id))
                    {
                        break;
                    }
                }
            }
        }

        List<PlanRowVm> rows = new();
        void Walk(PlanTask t, int level)
        {
            if (filtering && !visibleByFilter.Contains(t.Id))
            {
                return;
            }

            bool hasChildren = children.ContainsKey(t.Id);
            bool expanded = filtering || !_collapsed.Contains(t.Id);
            string predText = string.Join(", ", preds[t.Id].Select(l => byId.TryGetValue(l.PredecessorId, out PlanTask? p)
                ? p.Title + (l.LagDays != 0 ? " (" + (l.LagDays > 0 ? "+" : "") + l.LagDays + ")" : "")
                : "?"));
            rows.Add(new PlanRowVm(t, level, hasChildren, expanded, t.IsLate(today),
                t.VersionId is long v && versionLabels.TryGetValue(v, out string? label) ? label : null, predText,
                changeLinks.TryGetValue(t.Id, out List<long>? ch) ? ch.Count : 0, Calendar));
            if (hasChildren && expanded)
            {
                foreach (PlanTask c in children[t.Id])
                {
                    Walk(c, level + 1);
                }
            }
        }

        foreach (PlanTask r in roots)
        {
            Walk(r, 0);
        }

        Rows.Clear();
        foreach (PlanRowVm r in rows)
        {
            Rows.Add(r);
        }

        IsEmpty = _tasks.Count == 0;
        Selected = Rows.FirstOrDefault(r => r.Id == keep);
        BuildChart();
    }

    private bool Matches(PlanTask t, DateOnly today)
    {
        string f = Filter.Trim();
        if (f.Length > 0 && !t.Title.Contains(f, StringComparison.OrdinalIgnoreCase) &&
            !(t.Assignee?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false) &&
            !(t.Description?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return false;
        }

        if (AssigneeFilter != AllAssignees && !string.Equals(t.Assignee, AssigneeFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return StatusFilter switch
        {
            "Aperti" => !t.IsClosed,
            "In ritardo" => t.IsLate(today),
            "Da fare" => t.Status == PlanStatus.ToDo,
            "In corso" => t.Status == PlanStatus.InProgress,
            "Bloccati" => t.Status == PlanStatus.Blocked,
            "Fatti" => t.Status == PlanStatus.Done,
            _ => true,
        };
    }

    private void BuildChart()
    {
        List<GanttBar> bars = new();
        for (int i = 0; i < Rows.Count; i++)
        {
            PlanRowVm r = Rows[i];
            PlanTask t = r.Task;
            bars.Add(new GanttBar
            {
                Id = t.Id,
                Row = i,
                Start = t.Start,
                End = t.IsMilestone ? t.Start : t.End,
                Kind = t.Kind,
                Progress = t.Status == PlanStatus.Done ? 100 : t.Progress,
                Tone = t.IsMilestone ? (t.Status == PlanStatus.Done ? Tone.Success : Tone.Orange) : PlanStates.ToneOf(t.Status) == Tone.Neutral ? Tone.Accent : PlanStates.ToneOf(t.Status),
                Title = t.Title + (t.Assignee != null && !t.IsPhase ? "  · " + t.Assignee : ""),
                ToolTip = r.ToolTip,
                Late = r.IsLate,
                Cancelled = t.Status == PlanStatus.Cancelled,
                Draggable = !t.IsPhase,
            });
        }

        HashSet<long> shown = bars.Select(b => b.Id).ToHashSet();
        HashSet<long> violated = PlanScheduler.ViolatedLinks(_tasks, _links, Calendar);
        Bars = bars;
        Links = _links.Where(l => shown.Contains(l.PredecessorId) && shown.Contains(l.SuccessorId))
            .Select(l => new GanttLink(l.Id, l.PredecessorId, l.SuccessorId, violated.Contains(l.Id))).ToList();
    }

    private void UpdateKpi()
    {
        DateOnly today = GanttChart.Today;
        PlanKpi k = PlanKpi.Compute(_tasks, today, Calendar);
        if (k.Tasks == 0)
        {
            KpiText = "Nessuna attivita': \"Nuova attivita'\" o \"Nuova fase\" per cominciare.";
            LateChip = null;
            return;
        }

        List<string> parts = new() { k.Tasks + (k.Tasks == 1 ? " attivita'" : " attivita'"), k.ProgressPercent + "% completato" };
        if (k.NextMilestone != null)
        {
            parts.Add("prossima milestone: " + k.NextMilestone.Title + " " + k.NextMilestone.End.ToString("dd/MM", CultureInfo.InvariantCulture));
        }

        if (k.PlanEnd != null)
        {
            parts.Add("fine " + k.PlanEnd.Value.ToString("dd/MM/yy", CultureInfo.InvariantCulture));
        }

        if (k.EstimatedHours > 0 || k.ActualHours > 0)
        {
            parts.Add(k.ActualHours.ToString("0.#", CultureInfo.InvariantCulture) + " / " + k.EstimatedHours.ToString("0.#", CultureInfo.InvariantCulture) + " ore");
        }

        KpiText = string.Join(" · ", parts);
        LateChip = k.Late > 0 ? new ChipVm(k.Late + " in ritardo", Tone.Danger) : k.Blocked > 0 ? new ChipVm(k.Blocked + " bloccate", Tone.Warning) : null;
    }

    private void LoadHistory()
    {
        History.Clear();
        if (!ShowHistory)
        {
            return;
        }

        foreach (PlanEvent e in AppServices.Plans.Events(CommessaId, null, 500)
                     .Where(e => !OnlyDateHistory || e.Kind == PlanEventKinds.Dates || (e.Kind == PlanEventKinds.Undone && e.IsDates)))
        {
            History.Add(new PlanEventRow(e));
        }
    }

    [RelayCommand]
    private void ToggleExpand(PlanRowVm? row)
    {
        if (row is not { HasChildren: true })
        {
            return;
        }

        if (!_collapsed.Remove(row.Id))
        {
            _collapsed.Add(row.Id);
        }

        Rebuild();
    }

    // ---------- nuovi task ----------

    [RelayCommand]
    private void NewTask() => CreateNew(PlanKind.Task);

    [RelayCommand]
    private void NewMilestone() => CreateNew(PlanKind.Milestone);

    [RelayCommand]
    private void NewPhase() => CreateNew(PlanKind.Phase);

    private void CreateNew(PlanKind kind)
    {
        PlanTask? after = Selected?.Task;
        DateOnly start = after == null ? Calendar.NextWorkingDay(GanttChart.Today)
            : after.IsPhase ? Calendar.NextWorkingDay(after.Start)
            : PlanScheduler.EarliestStart(after.End, 0, Calendar);
        PlanTask t = new()
        {
            CommessaId = CommessaId,
            Kind = kind,
            Title = kind switch { PlanKind.Milestone => "Nuova milestone", PlanKind.Phase => "Nuova fase", _ => "Nuova attivita'" },
            Start = start,
            End = kind == PlanKind.Task ? Calendar.EndFor(start, 5) : start,
            // Dentro la fase selezionata, o accanto al task selezionato.
            ParentId = after == null ? null : after.IsPhase && Selected!.IsExpanded ? after.Id : after.ParentId,
            VersionId = HardwareService.Reference(_node.Commessa)?.Id,
        };
        if (kind != PlanKind.Task)
        {
            t.VersionId = null;
        }

        if (!TaskEditing.Edit(this, t, isNew: true))
        {
            return;
        }

        // Subito dopo la riga selezionata nell'ordine.
        List<PlanTask> ordered = _tasks.Where(x => x.Id != t.Id).OrderBy(x => x.Sort).ThenBy(x => x.Id).ToList();
        int at = after == null ? ordered.Count : ordered.FindIndex(x => x.Id == after.Id) + 1;
        ordered.Insert(Math.Clamp(at, 0, ordered.Count), t);
        AppServices.Plans.Reorder(CommessaId, ordered.Select((x, i) => (x.Id, x.Id == t.Id ? t.ParentId : x.ParentId, (i + 1) * 10)).ToList(), User);
        AfterChange("Creato: " + t.Title, t.Id);
    }

    [RelayCommand]
    private void Edit(PlanRowVm? row)
    {
        row ??= Selected;
        if (row == null)
        {
            return;
        }

        PlanTask? fresh = AppServices.Plans.Get(row.Id);
        if (fresh != null && TaskEditing.Edit(this, fresh, isNew: false))
        {
            AfterChange("Salvato: " + fresh.Title, fresh.Id);
        }
    }

    [RelayCommand]
    private void Delete()
    {
        PlanRowVm? row = Selected;
        if (row == null)
        {
            return;
        }

        string what = row.Task.IsPhase ? "la fase \"" + row.Title + "\" (le sue attivita' restano, un livello sopra)" : "\"" + row.Title + "\"";
        if (MessageBox.Show("Eliminare " + what + "?\nLo storico conserva il titolo e i cambiamenti.", "Elimina",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        AppServices.Plans.Delete(row.Id, User);
        AfterChange("Eliminato: " + row.Title, null);
    }

    // ---------- struttura ----------

    /// <summary>Dentro la fase che sta sopra (stesso livello).</summary>
    [RelayCommand]
    private void Indent()
    {
        PlanTask? t = Selected?.Task;
        if (t == null)
        {
            return;
        }

        List<PlanTask> siblings = Siblings(t);
        int i = siblings.FindIndex(x => x.Id == t.Id);
        PlanTask? phase = i > 0 ? siblings[i - 1] : null;
        if (phase is not { IsPhase: true })
        {
            Status = "Indenta: serve una fase subito sopra (\"Nuova fase\").";
            return;
        }

        List<PlanTask> kids = _tasks.Where(x => x.ParentId == phase.Id).OrderBy(x => x.Sort).ToList();
        int sort = kids.Count == 0 ? phase.Sort + 1 : kids.Max(x => x.Sort) + 1;
        _collapsed.Remove(phase.Id);
        AppServices.Plans.Reorder(CommessaId, new[] { (t.Id, (long?)phase.Id, sort) }, User);
        Renumber();
        AfterChange(t.Title + " nella fase " + phase.Title, t.Id);
    }

    /// <summary>Fuori dalla fase, subito dopo di essa.</summary>
    [RelayCommand]
    private void Outdent()
    {
        PlanTask? t = Selected?.Task;
        PlanTask? parent = t?.ParentId is long p ? _tasks.FirstOrDefault(x => x.Id == p) : null;
        if (t == null || parent == null)
        {
            return;
        }

        AppServices.Plans.Reorder(CommessaId, new[] { (t.Id, parent.ParentId, parent.Sort + 1) }, User);
        Renumber();
        AfterChange(t.Title + " fuori dalla fase " + parent.Title, t.Id);
    }

    [RelayCommand]
    private void MoveUp() => Move(-1);

    [RelayCommand]
    private void MoveDown() => Move(1);

    private void Move(int dir)
    {
        PlanTask? t = Selected?.Task;
        if (t == null)
        {
            return;
        }

        List<PlanTask> siblings = Siblings(t);
        int i = siblings.FindIndex(x => x.Id == t.Id), j = i + dir;
        if (j < 0 || j >= siblings.Count)
        {
            return;
        }

        (siblings[i], siblings[j]) = (siblings[j], siblings[i]);
        AppServices.Plans.Reorder(CommessaId, siblings.Select((x, k) => (x.Id, x.ParentId, (k + 1) * 10)).ToList(), User);
        AfterChange("", t.Id);
    }

    private List<PlanTask> Siblings(PlanTask t) =>
        _tasks.Where(x => x.ParentId == t.ParentId).OrderBy(x => x.Sort).ThenBy(x => x.Id).ToList();

    /// <summary>Ordine pulito (10, 20, 30...) seguendo l'albero.</summary>
    private void Renumber()
    {
        List<PlanTask> fresh = AppServices.Plans.Tasks(CommessaId);
        ILookup<long?, PlanTask> byParent = fresh.ToLookup(t => t.ParentId);
        List<(long, long?, int)> rows = new();
        int n = 0;
        void Walk(long? parent)
        {
            foreach (PlanTask t in byParent[parent].OrderBy(t => t.Sort).ThenBy(t => t.Id))
            {
                rows.Add((t.Id, t.ParentId, ++n * 10));
                Walk(t.Id);
            }
        }

        Walk(null);
        AppServices.Plans.Reorder(CommessaId, rows, User);
    }

    // ---------- dipendenze e date ----------

    /// <summary>Il selezionato comincia dopo la fine del task della riga sopra.</summary>
    [RelayCommand]
    private void LinkToPrevious()
    {
        PlanRowVm? row = Selected;
        int i = row == null ? -1 : Rows.IndexOf(row);
        PlanRowVm? prev = Enumerable.Range(0, Math.Max(0, i)).Select(k => Rows[i - 1 - k]).FirstOrDefault(r => !r.IsPhase);
        if (row == null || row.IsPhase || prev == null)
        {
            Status = "Collega: selezionare un'attivita' con un'altra attivita' sopra.";
            return;
        }

        if (_links.Any(l => l.PredecessorId == prev.Id && l.SuccessorId == row.Id))
        {
            Status = row.Title + " dipende gia' da " + prev.Title + ".";
            return;
        }

        if (PlanScheduler.WouldCreateCycle(_links, prev.Id, row.Id))
        {
            Notify.Warning("Collegamento non possibile", prev.Title + " dipende gia' (anche indirettamente) da " + row.Title + ".");
            return;
        }

        AppServices.Plans.AddLink(prev.Id, row.Id, 0, User);
        _links = AppServices.Plans.Links(CommessaId);
        ApplySchedule(new Dictionary<long, (DateOnly, DateOnly)>(), "Collegamento " + prev.Title + " → " + row.Title, quietIfNothing: true);
        AfterChange(row.Title + " dopo " + prev.Title, row.Id);
    }

    /// <summary>Trascinamento nel Gantt: cascata, motivo se sposta altri o ci sono conflitti, salvataggio, [Annulla].</summary>
    public void OnBarMoved(long id, DateOnly start, DateOnly end)
    {
        PlanTask? t = _tasks.FirstOrDefault(x => x.Id == id);
        if (t == null)
        {
            return;
        }

        string title = t.Title + ": " + start.ToString("dd/MM", CultureInfo.InvariantCulture) +
                       (t.IsMilestone ? "" : " - " + end.ToString("dd/MM", CultureInfo.InvariantCulture));
        ApplySchedule(new Dictionary<long, (DateOnly, DateOnly)> { [id] = (start, end) }, title, quietIfNothing: false);
        Reload();
    }

    /// <summary>
    /// Calcola la cascata delle date e la applica in un lotto: se sposta altri task o ci sono
    /// conflitti chiede conferma e motivo (ReasonWindow). Restituisce il lotto o null.
    /// </summary>
    public string? ApplySchedule(IReadOnlyDictionary<long, (DateOnly Start, DateOnly End)> edits, string action, bool quietIfNothing, string? reason = null)
    {
        List<PlanTask> current = AppServices.Plans.Tasks(CommessaId);
        ScheduleResult r = PlanScheduler.Propagate(current, AppServices.Plans.Links(CommessaId), edits, Calendar);
        if (r.HasCycle)
        {
            Notify.Error("Dipendenze circolari", "Le dipendenze formano un ciclo: togliere un collegamento dall'attivita'.");
            return null;
        }

        if (r.Changes.Count == 0)
        {
            if (!quietIfNothing)
            {
                Status = "Nessuna data cambiata.";
            }

            return null;
        }

        if (r.MovesOthers || r.Conflicts.Count > 0)
        {
            ReasonWindow w = new(r, action, reason) { Owner = Application.Current.MainWindow };
            if (w.ShowDialog() != true)
            {
                Status = "Spostamento annullato.";
                return null;
            }

            reason = w.Reason;
        }

        string batch = AppServices.Plans.ApplyDateChanges(CommessaId, r.Changes, reason, User);
        int others = r.Changes.Count(c => c.Cascade);
        Notify.Success("Date aggiornate",
            action + (others > 0 ? " · " + (others == 1 ? "1 altra attivita' spostata" : others + " altre attivita' spostate") : "") +
            (r.Conflicts.Count > 0 ? " · " + r.Conflicts.Count + " conflitti" : ""),
            new NoticeAction("Annulla", () => Undo(batch)));
        return batch;
    }

    [RelayCommand]
    private void UndoLast()
    {
        string? batch = AppServices.Plans.LastUndoableBatch(CommessaId);
        if (batch == null)
        {
            Status = "Niente da annullare.";
            return;
        }

        Undo(batch);
    }

    private void Undo(string batch)
    {
        PlanUndoResult r = AppServices.Plans.UndoBatch(batch, User);
        if (r.Ok)
        {
            Notify.Info(r.Message);
            Reload();
        }
        else
        {
            Notify.Warning("Annullamento non possibile", r.Message);
        }
    }

    [RelayCommand]
    private Task Export(string? format)
    {
        IReadOnlyCollection<DocFormat> formats = format switch
        {
            "docx" => new[] { DocFormat.Docx },
            "pdf" => new[] { DocFormat.Pdf },
            "csv" => new[] { DocFormat.Csv },
            _ => DocExporter.All.ToArray(),
        };
        return DocumentService.ExportAsync(CommessaId, null, new[] { DocKinds.Plan }, formats);
    }

    private void AfterChange(string status, long? select)
    {
        Reload();
        if (select != null)
        {
            Selected = Rows.FirstOrDefault(r => r.Id == select);
        }

        if (status.Length > 0)
        {
            Status = status;
        }

        if (_main.Detail is CommessaDetailViewModel cd && cd.Node == _node)
        {
            cd.Docs.Invalidate();
        }
    }

    // ---------- per l'editor ----------

    public IReadOnlyList<PlanTask> AllTasks => _tasks;

    public IReadOnlyList<PlanLink> AllLinks => _links;

    public CommessaNode Node => _node;
}
