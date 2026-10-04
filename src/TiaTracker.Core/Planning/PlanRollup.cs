namespace TiaTracker.Core.Planning;

/// <summary>Le fasi prendono date, avanzamento e stato dai figli (anche fasi dentro fasi).</summary>
public static class PlanRollup
{
    public static void Apply(IReadOnlyList<PlanTask> tasks, WorkCalendar cal)
    {
        Dictionary<long, List<PlanTask>> children = tasks.Where(t => t.ParentId != null)
            .GroupBy(t => t.ParentId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        HashSet<long> done = new();
        foreach (PlanTask phase in tasks.Where(t => t.IsPhase))
        {
            Compute(phase, children, cal, done, new HashSet<long>());
        }
    }

    private static void Compute(PlanTask phase, Dictionary<long, List<PlanTask>> children, WorkCalendar cal, HashSet<long> done, HashSet<long> path)
    {
        if (done.Contains(phase.Id) || !path.Add(phase.Id))
        {
            return; // gia' calcolata, o fase che contiene se stessa (dati rovinati)
        }

        List<PlanTask> kids = (children.GetValueOrDefault(phase.Id) ?? new List<PlanTask>()).Where(k => k.Status != PlanStatus.Cancelled).ToList();
        foreach (PlanTask k in kids.Where(k => k.IsPhase))
        {
            Compute(k, children, cal, done, path);
        }

        done.Add(phase.Id);
        if (kids.Count == 0)
        {
            return; // fase vuota: tiene le sue date
        }

        phase.Start = kids.Min(k => k.Start);
        phase.End = kids.Max(k => k.End);
        double weight = 0, sum = 0;
        foreach (PlanTask k in kids)
        {
            double w = k.IsMilestone ? 1 : cal.Duration(k.Start, k.End);
            weight += w;
            sum += w * (k.Status == PlanStatus.Done ? 100 : k.Progress);
        }

        phase.Progress = weight > 0 ? (int)Math.Round(sum / weight) : 0;
        phase.Status = kids.All(k => k.Status == PlanStatus.Done) ? PlanStatus.Done
            : kids.Any(k => k.Status == PlanStatus.Blocked) ? PlanStatus.Blocked
            : kids.Any(k => k.Status is PlanStatus.InProgress or PlanStatus.Done || k.Progress > 0) ? PlanStatus.InProgress
            : PlanStatus.ToDo;
        phase.EstimatedHours = kids.Any(k => k.EstimatedHours != null) ? kids.Sum(k => k.EstimatedHours ?? 0) : null;
        phase.ActualHours = kids.Any(k => k.ActualHours != null) ? kids.Sum(k => k.ActualHours ?? 0) : null;
    }
}

/// <summary>I numeri della pianificazione per la dashboard.</summary>
public sealed record PlanKpi(
    int Tasks,
    int Done,
    int InProgress,
    int Blocked,
    int Late,
    int DueThisWeek,
    int ProgressPercent,
    PlanTask? NextMilestone,
    double EstimatedHours,
    double ActualHours,
    DateOnly? PlanEnd)
{
    public static PlanKpi Compute(IReadOnlyList<PlanTask> tasks, DateOnly today, WorkCalendar cal)
    {
        List<PlanTask> work = tasks.Where(t => !t.IsPhase && t.Status != PlanStatus.Cancelled).ToList();
        double weight = 0, sum = 0;
        foreach (PlanTask t in work)
        {
            double w = t.IsMilestone ? 1 : cal.Duration(t.Start, t.End);
            weight += w;
            sum += w * (t.Status == PlanStatus.Done ? 100 : t.Progress);
        }

        DateOnly weekEnd = today.AddDays(((int)DayOfWeek.Sunday - (int)today.DayOfWeek + 7) % 7);
        return new PlanKpi(
            work.Count,
            work.Count(t => t.Status == PlanStatus.Done),
            work.Count(t => t.Status == PlanStatus.InProgress),
            work.Count(t => t.Status == PlanStatus.Blocked),
            work.Count(t => t.IsLate(today)),
            work.Count(t => !t.IsClosed && t.End >= today && t.End <= weekEnd),
            weight > 0 ? (int)Math.Round(sum / weight) : 0,
            work.Where(t => t.IsMilestone && !t.IsClosed).OrderBy(t => t.End).FirstOrDefault(),
            work.Sum(t => t.EstimatedHours ?? 0),
            work.Sum(t => t.ActualHours ?? 0),
            work.Count > 0 ? work.Max(t => t.End) : null);
    }
}
