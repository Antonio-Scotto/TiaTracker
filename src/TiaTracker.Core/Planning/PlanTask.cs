using TiaTracker.Core.Domain;

namespace TiaTracker.Core.Planning;

public enum PlanKind
{
    Task,
    Milestone,

    /// <summary>Fase: raggruppa altri task, date e avanzamento calcolati dai figli.</summary>
    Phase,
}

public enum PlanStatus
{
    ToDo,
    InProgress,
    Blocked,
    Done,
    Cancelled,
}

public enum PlanPriority
{
    Low,
    Normal,
    High,
    Urgent,
}

/// <summary>Un'attivita', una milestone o una fase della pianificazione di una commessa.</summary>
public sealed class PlanTask
{
    public long Id { get; set; }
    public long CommessaId { get; set; }

    /// <summary>La fase che lo contiene (null = primo livello).</summary>
    public long? ParentId { get; set; }
    public int Sort { get; set; }
    public PlanKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public PlanStatus Status { get; set; }
    public PlanPriority Priority { get; set; } = PlanPriority.Normal;
    public string? Assignee { get; set; }

    /// <summary>Primo e ultimo giorno (compresi); per una milestone sono uguali.</summary>
    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }

    /// <summary>Data fissata: la cascata delle dipendenze non la sposta (se dovrebbe, e' un conflitto).</summary>
    public bool Locked { get; set; }

    /// <summary>Avanzamento 0-100.</summary>
    public int Progress { get; set; }
    public double? EstimatedHours { get; set; }
    public double? ActualHours { get; set; }

    /// <summary>La versione del progetto TIA a cui si riferisce (es. le modifiche di V0.70).</summary>
    public long? VersionId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public bool IsMilestone => Kind == PlanKind.Milestone;

    public bool IsPhase => Kind == PlanKind.Phase;

    /// <summary>Chiuso: fatto o annullato (non si sposta, non e' in ritardo).</summary>
    public bool IsClosed => Status is PlanStatus.Done or PlanStatus.Cancelled;

    public bool IsLate(DateOnly today) => !IsClosed && !IsPhase && End < today;

    public PlanTask Clone() => (PlanTask)MemberwiseClone();
}

/// <summary>Dipendenza fine-inizio: il successore comincia dopo la fine del predecessore piu' <see cref="LagDays"/> giorni lavorativi.</summary>
public sealed record PlanLink(long Id, long PredecessorId, long SuccessorId, int LagDays = 0);

public static class PlanStates
{
    public static string ToDb(PlanKind k) => k switch
    {
        PlanKind.Milestone => "milestone",
        PlanKind.Phase => "fase",
        _ => "attivita",
    };

    public static PlanKind KindFromDb(string? s) => s switch
    {
        "milestone" => PlanKind.Milestone,
        "fase" => PlanKind.Phase,
        _ => PlanKind.Task,
    };

    public static string Italian(PlanKind k) => k switch
    {
        PlanKind.Milestone => "Milestone",
        PlanKind.Phase => "Fase",
        _ => "Attivita'",
    };

    public static string ToDb(PlanStatus s) => s switch
    {
        PlanStatus.InProgress => "in_corso",
        PlanStatus.Blocked => "bloccata",
        PlanStatus.Done => "fatta",
        PlanStatus.Cancelled => "annullata",
        _ => "da_fare",
    };

    public static PlanStatus StatusFromDb(string? s) => s switch
    {
        "in_corso" => PlanStatus.InProgress,
        "bloccata" => PlanStatus.Blocked,
        "fatta" => PlanStatus.Done,
        "annullata" => PlanStatus.Cancelled,
        _ => PlanStatus.ToDo,
    };

    public static string Italian(PlanStatus s) => s switch
    {
        PlanStatus.InProgress => "In corso",
        PlanStatus.Blocked => "Bloccata",
        PlanStatus.Done => "Fatta",
        PlanStatus.Cancelled => "Annullata",
        _ => "Da fare",
    };

    public static Tone ToneOf(PlanStatus s) => s switch
    {
        PlanStatus.InProgress => Tone.Accent,
        PlanStatus.Blocked => Tone.Danger,
        PlanStatus.Done => Tone.Success,
        PlanStatus.Cancelled => Tone.Muted,
        _ => Tone.Neutral,
    };

    public static string ToDb(PlanPriority p) => p switch
    {
        PlanPriority.Low => "bassa",
        PlanPriority.High => "alta",
        PlanPriority.Urgent => "urgente",
        _ => "normale",
    };

    public static PlanPriority PriorityFromDb(string? s) => s switch
    {
        "bassa" => PlanPriority.Low,
        "alta" => PlanPriority.High,
        "urgente" => PlanPriority.Urgent,
        _ => PlanPriority.Normal,
    };

    public static string Italian(PlanPriority p) => p switch
    {
        PlanPriority.Low => "Bassa",
        PlanPriority.High => "Alta",
        PlanPriority.Urgent => "Urgente",
        _ => "Normale",
    };

    public static Tone ToneOf(PlanPriority p) => p switch
    {
        PlanPriority.Low => Tone.Muted,
        PlanPriority.High => Tone.Orange,
        PlanPriority.Urgent => Tone.Danger,
        _ => Tone.Neutral,
    };

    public static readonly IReadOnlyList<PlanStatus> AllStatuses =
        new[] { PlanStatus.ToDo, PlanStatus.InProgress, PlanStatus.Blocked, PlanStatus.Done, PlanStatus.Cancelled };

    public static readonly IReadOnlyList<PlanPriority> AllPriorities =
        new[] { PlanPriority.Low, PlanPriority.Normal, PlanPriority.High, PlanPriority.Urgent };

    public static readonly IReadOnlyList<PlanKind> AllKinds = new[] { PlanKind.Task, PlanKind.Milestone, PlanKind.Phase };
}
