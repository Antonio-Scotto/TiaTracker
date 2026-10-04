using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TiaTracker.App.Services;
using TiaTracker.App.Views;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Planning;

namespace TiaTracker.App.ViewModels;

/// <summary>Una voce di tendina: valore e testo.</summary>
public sealed record Choice<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>Un predecessore nell'editor: esistente (LinkId &gt; 0) o da aggiungere.</summary>
public sealed partial class LinkRow : ObservableObject
{
    public LinkRow(long linkId, PlanTask predecessor, int lag)
    {
        LinkId = linkId;
        Predecessor = predecessor;
        Lag = lag;
        OriginalLag = lag;
    }

    public long LinkId { get; }

    public PlanTask Predecessor { get; }

    public int OriginalLag { get; }

    public string Title => Predecessor.Title;

    public string Dates => Predecessor.IsMilestone
        ? Predecessor.Start.ToString("dd/MM/yy", CultureInfo.InvariantCulture)
        : Predecessor.Start.ToString("dd/MM", CultureInfo.InvariantCulture) + " - " + Predecessor.End.ToString("dd/MM/yy", CultureInfo.InvariantCulture);

    [ObservableProperty]
    public partial int Lag { get; set; }
}

/// <summary>Una modifica del registro con la casella "collegata a questo task".</summary>
public sealed partial class ChangeChoice : ObservableObject
{
    public ChangeChoice(Change change, bool linked, string versions)
    {
        Change = change;
        IsLinked = linked;
        Versions = versions;
    }

    public Change Change { get; }

    public string Title => Change.Title;

    public string Info => (Change.Date?.ToString("dd/MM/yy", CultureInfo.InvariantCulture) ?? "") + (Versions.Length > 0 ? " · " + Versions : "");

    public string Versions { get; }

    [ObservableProperty]
    public partial bool IsLinked { get; set; }
}

/// <summary>Editor di un'attivita', milestone o fase: dati, dipendenze, modifiche collegate, storico.</summary>
public sealed partial class TaskEditorViewModel : ObservableObject
{
    private readonly PlanTask _original;
    private readonly WorkCalendar _cal;
    private readonly IReadOnlyList<PlanLink> _links;
    private readonly HashSet<long> _removedLinks = new();
    private bool _syncing;

    public TaskEditorViewModel(PlanTask task, bool isNew, IReadOnlyList<PlanTask> all, IReadOnlyList<PlanLink> links, IReadOnlyList<ProjectVersion> versions,
        IReadOnlyList<Change> changes, IReadOnlyCollection<long> linkedChanges, IReadOnlyList<string> assignees, IReadOnlyList<PlanEvent> history,
        WorkCalendar cal)
    {
        _original = task;
        _cal = cal;
        _links = links;
        IsNew = isNew;

        Kinds = PlanStates.AllKinds.Select(k => new Choice<PlanKind>(k, PlanStates.Italian(k))).ToList();
        Statuses = PlanStates.AllStatuses.Select(s => new Choice<PlanStatus>(s, PlanStates.Italian(s))).ToList();
        Priorities = PlanStates.AllPriorities.Select(p => new Choice<PlanPriority>(p, PlanStates.Italian(p))).ToList();
        Versions = new List<Choice<long?>> { new(null, "(nessuna)") };
        Versions.AddRange(versions.Where(v => !v.Missing).OrderByDescending(v => v.SortKey, StringComparer.Ordinal)
            .Select(v => new Choice<long?>(v.Id, v.Label + (v.State != VersionState.None ? " · " + VersionStates.Italian(v.State) : ""))));

        // Fasi possibili: tutte tranne se stessa e quelle che contiene.
        HashSet<long> inside = Descendants(task.Id, all);
        Phases = new List<Choice<long?>> { new(null, "(nessuna: primo livello)") };
        Phases.AddRange(all.Where(t => t.IsPhase && t.Id != task.Id && !inside.Contains(t.Id)).OrderBy(t => t.Sort).Select(t => new Choice<long?>(t.Id, t.Title)));
        AssigneeSuggestions = assignees.ToList();

        Kind = Kinds.First(k => k.Value == task.Kind);
        Title = task.Title;
        Description = task.Description ?? "";
        Status = Statuses.First(s => s.Value == task.Status);
        Priority = Priorities.First(p => p.Value == task.Priority);
        Assignee = task.Assignee ?? "";
        _syncing = true;
        StartDate = task.Start.ToDateTime(TimeOnly.MinValue);
        EndDate = (task.IsMilestone ? task.Start : task.End).ToDateTime(TimeOnly.MinValue);
        Duration = cal.Duration(task.Start, task.End);
        _syncing = false;
        Locked = task.Locked;
        Progress = task.Progress;
        EstimatedHours = Hours(task.EstimatedHours);
        ActualHours = Hours(task.ActualHours);
        Version = Versions.FirstOrDefault(v => v.Value == task.VersionId) ?? Versions[0];
        Phase = Phases.FirstOrDefault(p => p.Value == task.ParentId) ?? Phases[0];

        Dictionary<long, PlanTask> byId = all.ToDictionary(t => t.Id);
        foreach (PlanLink l in links.Where(l => l.SuccessorId == task.Id && byId.ContainsKey(l.PredecessorId)))
        {
            Predecessors.Add(new LinkRow(l.Id, byId[l.PredecessorId], l.LagDays));
        }

        Candidates = all.Where(t => !t.IsPhase && t.Id != task.Id).OrderBy(t => t.Start).ThenBy(t => t.Title).ToList();

        Dictionary<long, string> labels = versions.ToDictionary(v => v.Id, v => v.Label);
        foreach (Change c in changes.OrderByDescending(c => c.Date ?? DateOnly.MinValue).ThenByDescending(c => c.Id))
        {
            string vs = string.Join(", ", c.Versions.Where(v => labels.ContainsKey(v.VersionId)).Select(v => labels[v.VersionId]));
            Changes.Add(new ChangeChoice(c, linkedChanges.Contains(c.Id), vs));
        }

        ChangesView = CollectionViewSource.GetDefaultView(Changes);
        ChangesView.Filter = o => o is ChangeChoice c && (ChangeFilter.Length == 0 || c.IsLinked ||
                                                          c.Title.Contains(ChangeFilter, StringComparison.OrdinalIgnoreCase));
        History = history.Select(e => new PlanEventRow(e)).ToList();
    }

    public bool IsNew { get; }

    public string WindowTitle => IsNew ? "Nuova " + PlanStates.Italian(Kind.Value).ToLowerInvariant() : Title;

    public List<Choice<PlanKind>> Kinds { get; }

    public List<Choice<PlanStatus>> Statuses { get; }

    public List<Choice<PlanPriority>> Priorities { get; }

    public List<Choice<long?>> Versions { get; }

    public List<Choice<long?>> Phases { get; }

    public List<string> AssigneeSuggestions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMilestone), nameof(IsPhase), nameof(HasDuration), nameof(DatesEditable), nameof(WindowTitle))]
    public partial Choice<PlanKind> Kind { get; set; }

    public bool IsMilestone => Kind.Value == PlanKind.Milestone;

    public bool IsPhase => Kind.Value == PlanKind.Phase;

    public bool HasDuration => Kind.Value == PlanKind.Task;

    /// <summary>Le date delle fasi vengono dai figli.</summary>
    public bool DatesEditable => !IsPhase;

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string Description { get; set; } = "";

    [ObservableProperty]
    public partial Choice<PlanStatus> Status { get; set; }

    [ObservableProperty]
    public partial Choice<PlanPriority> Priority { get; set; }

    [ObservableProperty]
    public partial string Assignee { get; set; } = "";

    [ObservableProperty]
    public partial DateTime? StartDate { get; set; }

    [ObservableProperty]
    public partial DateTime? EndDate { get; set; }

    /// <summary>Durata in giorni lavorativi: cambiandola si sposta la fine.</summary>
    [ObservableProperty]
    public partial int Duration { get; set; }

    [ObservableProperty]
    public partial bool Locked { get; set; }

    [ObservableProperty]
    public partial int Progress { get; set; }

    [ObservableProperty]
    public partial string EstimatedHours { get; set; } = "";

    [ObservableProperty]
    public partial string ActualHours { get; set; } = "";

    [ObservableProperty]
    public partial Choice<long?> Version { get; set; }

    [ObservableProperty]
    public partial Choice<long?> Phase { get; set; }

    /// <summary>Nota per lo storico (es. perche' le date sono cambiate).</summary>
    [ObservableProperty]
    public partial string Reason { get; set; } = "";

    [ObservableProperty]
    public partial string? Error { get; set; }

    public ObservableCollection<LinkRow> Predecessors { get; } = new();

    public List<PlanTask> Candidates { get; }

    [ObservableProperty]
    public partial PlanTask? NewPredecessor { get; set; }

    [ObservableProperty]
    public partial int NewLag { get; set; }

    public ObservableCollection<ChangeChoice> Changes { get; } = new();

    public ICollectionView ChangesView { get; }

    [ObservableProperty]
    public partial string ChangeFilter { get; set; } = "";

    public List<PlanEventRow> History { get; }

    public string WorkingDaysHint => StartDate is DateTime s && EndDate is DateTime e
        ? _cal.WorkingDays(DateOnly.FromDateTime(s), DateOnly.FromDateTime(e)) + " giorni lavorativi"
        : "";

    partial void OnChangeFilterChanged(string value) => ChangesView.Refresh();

    partial void OnStartDateChanged(DateTime? value)
    {
        if (_syncing || value == null)
        {
            return;
        }

        // Spostando l'inizio la durata resta: si sposta anche la fine.
        _syncing = true;
        DateOnly start = DateOnly.FromDateTime(value.Value);
        EndDate = (IsMilestone ? start : _cal.EndFor(start, Math.Max(1, Duration))).ToDateTime(TimeOnly.MinValue);
        _syncing = false;
        OnPropertyChanged(nameof(WorkingDaysHint));
    }

    partial void OnEndDateChanged(DateTime? value)
    {
        if (_syncing || value == null || StartDate == null)
        {
            return;
        }

        _syncing = true;
        Duration = _cal.Duration(DateOnly.FromDateTime(StartDate.Value), DateOnly.FromDateTime(value.Value));
        _syncing = false;
        OnPropertyChanged(nameof(WorkingDaysHint));
    }

    partial void OnDurationChanged(int value)
    {
        if (_syncing || StartDate == null || value < 1)
        {
            return;
        }

        _syncing = true;
        EndDate = _cal.EndFor(DateOnly.FromDateTime(StartDate.Value), value).ToDateTime(TimeOnly.MinValue);
        _syncing = false;
        OnPropertyChanged(nameof(WorkingDaysHint));
    }

    partial void OnStatusChanged(Choice<PlanStatus> value)
    {
        if (value.Value == PlanStatus.Done && !IsPhase)
        {
            Progress = 100;
        }
    }

    [RelayCommand]
    private void AddPredecessor()
    {
        if (NewPredecessor == null)
        {
            return;
        }

        if (Predecessors.Any(p => p.Predecessor.Id == NewPredecessor.Id))
        {
            Error = "\"" + NewPredecessor.Title + "\" e' gia' fra i predecessori.";
            return;
        }

        // Ciclo: con i collegamenti attuali, meno quelli tolti, piu' quelli nuovi.
        List<PlanLink> links = _links.Where(l => !_removedLinks.Contains(l.Id)).ToList();
        links.AddRange(Predecessors.Where(p => p.LinkId == 0).Select(p => new PlanLink(0, p.Predecessor.Id, _original.Id, p.Lag)));
        if (_original.Id != 0 && PlanScheduler.WouldCreateCycle(links, NewPredecessor.Id, _original.Id))
        {
            Error = "\"" + NewPredecessor.Title + "\" dipende gia' (anche indirettamente) da questa attivita'.";
            return;
        }

        Predecessors.Add(new LinkRow(0, NewPredecessor, NewLag));
        NewPredecessor = null;
        NewLag = 0;
        Error = null;
    }

    [RelayCommand]
    private void RemovePredecessor(LinkRow? row)
    {
        if (row == null)
        {
            return;
        }

        if (row.LinkId > 0)
        {
            _removedLinks.Add(row.LinkId);
        }

        Predecessors.Remove(row);
    }

    public IReadOnlyCollection<long> RemovedLinks => _removedLinks;

    public IEnumerable<(long Predecessor, int Lag)> AddedLinks => Predecessors.Where(p => p.LinkId == 0).Select(p => (p.Predecessor.Id, p.Lag));

    public IEnumerable<(long LinkId, int Lag)> ChangedLags => Predecessors.Where(p => p.LinkId > 0 && p.Lag != p.OriginalLag).Select(p => (p.LinkId, p.Lag));

    public List<long> LinkedChangeIds => Changes.Where(c => c.IsLinked).Select(c => c.Change.Id).ToList();

    public bool Validate()
    {
        Error = null;
        if (Title.Trim().Length == 0)
        {
            Error = "Il titolo e' obbligatorio.";
        }
        else if (StartDate == null || (!IsMilestone && EndDate == null))
        {
            Error = "Indicare le date.";
        }
        else if (!IsMilestone && EndDate < StartDate)
        {
            Error = "La fine viene prima dell'inizio.";
        }
        else if (!TryHours(EstimatedHours, out _) || !TryHours(ActualHours, out _))
        {
            Error = "Ore non valide (es. 12 o 7,5).";
        }

        return Error == null;
    }

    public PlanTask ToTask()
    {
        PlanTask t = _original.Clone();
        t.Kind = Kind.Value;
        t.Title = Title.Trim();
        t.Description = Description.Trim().Length == 0 ? null : Description.Trim();
        t.Status = Status.Value;
        t.Priority = Priority.Value;
        t.Assignee = Assignee.Trim().Length == 0 ? null : Assignee.Trim();
        if (!IsPhase)
        {
            t.Start = DateOnly.FromDateTime(StartDate!.Value);
            t.End = IsMilestone ? t.Start : DateOnly.FromDateTime(EndDate!.Value);
        }

        t.Locked = Locked;
        t.Progress = IsMilestone ? (Status.Value == PlanStatus.Done ? 100 : 0) : Math.Clamp(Progress, 0, 100);
        TryHours(EstimatedHours, out double? est);
        TryHours(ActualHours, out double? act);
        t.EstimatedHours = est;
        t.ActualHours = act;
        t.VersionId = Version.Value;
        t.ParentId = Phase.Value;
        return t;
    }

    private static string Hours(double? h) => h?.ToString("0.##", CultureInfo.GetCultureInfo("it-IT")) ?? "";

    private static bool TryHours(string text, out double? hours)
    {
        hours = null;
        string s = text.Trim().Replace(',', '.');
        if (s.Length == 0)
        {
            return true;
        }

        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v >= 0)
        {
            hours = v;
            return true;
        }

        return false;
    }

    private static HashSet<long> Descendants(long id, IReadOnlyList<PlanTask> all)
    {
        HashSet<long> found = new();
        Stack<long> stack = new();
        stack.Push(id);
        while (stack.Count > 0)
        {
            long p = stack.Pop();
            foreach (PlanTask c in all.Where(t => t.ParentId == p))
            {
                if (found.Add(c.Id))
                {
                    stack.Push(c.Id);
                }
            }
        }

        return found;
    }
}

/// <summary>Apertura dell'editor e salvataggio: campi, date con cascata, dipendenze, modifiche collegate.</summary>
public static class TaskEditing
{
    public static bool Edit(PlanningViewModel planning, PlanTask task, bool isNew)
    {
        long c = planning.CommessaId;
        List<ProjectVersion> versions = AppServices.Versions.ByCommessa(c);
        List<Change> changes = AppServices.Changes.ByCommessa(c, includeDrafts: false);
        List<long> linked = isNew ? new List<long>() : AppServices.Plans.ChangeLinks(c).GetValueOrDefault(task.Id) ?? new List<long>();
        List<PlanEvent> history = isNew ? new List<PlanEvent>() : AppServices.Plans.Events(c, task.Id, 300);
        TaskEditorViewModel vm = new(task, isNew, planning.AllTasks, planning.AllLinks, versions, changes, linked, AppServices.Plans.Assignees(c), history,
            planning.Calendar);
        TaskEditorWindow window = new(vm) { Owner = Application.Current.MainWindow };
        if (window.ShowDialog() != true)
        {
            return false;
        }

        PlanTask edited = vm.ToTask();
        string user = PlanningViewModel.User;
        string? reason = vm.Reason.Trim().Length == 0 ? null : vm.Reason.Trim();
        bool wasDone = !isNew && task.Status == PlanStatus.Done;
        if (isNew)
        {
            AppServices.Plans.Insert(edited, user);
            task.Id = edited.Id;
        }
        else if (edited.Start != task.Start || edited.End != task.End)
        {
            // Prima i campi, poi le date con la cascata (che puo' chiedere conferma e motivo).
            PlanTask fields = edited.Clone();
            fields.Start = task.Start;
            fields.End = task.End;
            AppServices.Plans.Update(fields, user, reason);
            planning.ApplySchedule(new Dictionary<long, (DateOnly, DateOnly)> { [edited.Id] = (edited.Start, edited.End) },
                edited.Title + ": nuove date", quietIfNothing: true, reason);
        }
        else
        {
            AppServices.Plans.Update(edited, user, reason);
        }

        bool linksChanged = false;
        foreach (long id in vm.RemovedLinks)
        {
            AppServices.Plans.RemoveLink(id, user);
            linksChanged = true;
        }

        foreach ((long pred, int lag) in vm.AddedLinks)
        {
            AppServices.Plans.AddLink(pred, edited.Id, lag, user);
            linksChanged = true;
        }

        foreach ((long linkId, int lag) in vm.ChangedLags)
        {
            AppServices.Plans.SetLinkLag(linkId, lag);
            linksChanged = true;
        }

        if (linksChanged)
        {
            planning.ApplySchedule(new Dictionary<long, (DateOnly, DateOnly)>(), "Dipendenze di " + edited.Title, quietIfNothing: true, reason);
        }

        List<long> changeIds = vm.LinkedChangeIds;
        AppServices.Plans.SetChanges(edited.Id, changeIds);

        // Lavoro fatto su una versione ma senza modifiche collegate: proposta di registrarlo.
        if (edited.Status == PlanStatus.Done && !wasDone && edited.Kind == PlanKind.Task && edited.VersionId != null && changeIds.Count == 0)
        {
            string label = versions.FirstOrDefault(v => v.Id == edited.VersionId)?.Label ?? "";
            if (MessageBox.Show("\"" + edited.Title + "\" e' fatta su " + label + ".\n\nRegistrare la modifica nel registro delle modifiche?",
                    "Attivita' fatta", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes)
            {
                long? changeId = ChangeEditing.NewPrefilled(c, edited.VersionId, edited.Title, edited.Description);
                if (changeId != null)
                {
                    AppServices.Plans.SetChanges(edited.Id, new[] { changeId.Value });
                    OutputService.Refresh(c);
                }
            }
        }

        return true;
    }
}
