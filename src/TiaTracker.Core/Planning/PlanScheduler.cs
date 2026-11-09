using System.Globalization;

namespace TiaTracker.Core.Planning;

/// <summary>Un task che cambia date: per mano dell'utente o spinto dalla cascata delle dipendenze.</summary>
public sealed record DateChange(long TaskId, string Title, DateOnly OldStart, DateOnly OldEnd, DateOnly NewStart, DateOnly NewEnd, bool Cascade)
{
    /// <summary>Spostamento dell'inizio in giorni di calendario (positivo = piu' tardi).</summary>
    public int ShiftDays => NewStart.DayNumber - OldStart.DayNumber;

    public bool Resized => NewEnd.DayNumber - NewStart.DayNumber != OldEnd.DayNumber - OldStart.DayNumber;
}

/// <summary>Un vincolo che la cascata non puo' rispettare (task fissato, chiuso, o spostato a mano troppo presto).</summary>
public sealed record PlanConflict(long TaskId, string Title, string Reason);

public sealed class ScheduleResult
{
    public List<DateChange> Changes { get; } = new();
    public List<PlanConflict> Conflicts { get; } = new();

    /// <summary>Le dipendenze formano un ciclo: nessuna cascata calcolata.</summary>
    public bool HasCycle { get; init; }

    /// <summary>Lo spostamento tocca altri task oltre a quelli cambiati a mano.</summary>
    public bool MovesOthers => Changes.Any(c => c.Cascade);
}

/// <summary>
/// Pianificazione con dipendenze fine-inizio: chi dipende da un task comincia
/// il giorno lavorativo dopo la sua fine, piu' il ritardo. Quando un task
/// slitta, i successori slittano con lui conservando la durata in giorni
/// lavorativi; mai all'indietro (anticipare un task non anticipa gli altri).
/// </summary>
public static class PlanScheduler
{
    public static ScheduleResult Propagate(IReadOnlyList<PlanTask> tasks, IReadOnlyList<PlanLink> links,
        IReadOnlyDictionary<long, (DateOnly Start, DateOnly End)> edits, WorkCalendar cal)
    {
        Dictionary<long, PlanTask> byId = tasks.ToDictionary(t => t.Id);
        Dictionary<long, (DateOnly Start, DateOnly End)> dates = tasks.ToDictionary(t => t.Id, t => (t.Start, t.End));
        foreach (KeyValuePair<long, (DateOnly Start, DateOnly End)> e in edits)
        {
            if (dates.ContainsKey(e.Key))
            {
                dates[e.Key] = e.Value;
            }
        }

        List<PlanLink> valid = links.Where(l => l.PredecessorId != l.SuccessorId && byId.ContainsKey(l.PredecessorId) && byId.ContainsKey(l.SuccessorId))
            .ToList();
        List<long>? order = TopologicalOrder(byId.Keys, valid);
        ScheduleResult result = new() { HasCycle = order == null };
        if (order != null)
        {
            Dictionary<long, List<PlanLink>> incoming = valid.GroupBy(l => l.SuccessorId).ToDictionary(g => g.Key, g => g.ToList());
            foreach (long id in order)
            {
                PlanTask t = byId[id];
                if (t.IsPhase || !incoming.TryGetValue(id, out List<PlanLink>? preds))
                {
                    continue;
                }

                DateOnly earliest = DateOnly.MinValue;
                PlanTask? by = null;
                foreach (PlanLink l in preds)
                {
                    PlanTask p = byId[l.PredecessorId];
                    if (p.Status == PlanStatus.Cancelled)
                    {
                        continue;
                    }

                    DateOnly min = EarliestStart(dates[p.Id].End, l.LagDays, cal);
                    if (min > earliest)
                    {
                        earliest = min;
                        by = p;
                    }
                }

                (DateOnly start, DateOnly end) = dates[id];
                if (by == null || start >= earliest)
                {
                    continue;
                }

                string when = earliest.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
                if (edits.ContainsKey(id))
                {
                    result.Conflicts.Add(new PlanConflict(id, t.Title, "comincia prima della fine di \"" + by.Title + "\" (potrebbe dal " + when + ")"));
                    continue;
                }

                if (t.Locked || t.IsClosed)
                {
                    string why = t.Locked ? "data fissata" : PlanStates.Italian(t.Status).ToLowerInvariant();
                    result.Conflicts.Add(new PlanConflict(id, t.Title, why + ": per \"" + by.Title + "\" dovrebbe slittare al " + when));
                    continue;
                }

                int duration = cal.Duration(start, end);
                DateOnly newStart = cal.NextWorkingDay(earliest);
                dates[id] = (newStart, t.IsMilestone ? newStart : cal.EndFor(newStart, duration));
            }
        }

        foreach (PlanTask t in tasks)
        {
            (DateOnly s, DateOnly e) = dates[t.Id];
            if (s != t.Start || e != t.End)
            {
                result.Changes.Add(new DateChange(t.Id, t.Title, t.Start, t.End, s, e, !edits.ContainsKey(t.Id)));
            }
        }

        return result;
    }

    /// <summary>Primo giorno possibile per il successore: il lavorativo dopo la fine del predecessore, piu' il ritardo (o meno l'anticipo).</summary>
    public static DateOnly EarliestStart(DateOnly predecessorEnd, int lagDays, WorkCalendar cal)
    {
        DateOnly d = cal.NextWorkingDay(predecessorEnd.AddDays(1));
        return lagDays == 0 ? d : cal.AddWorkingDays(d, lagDays);
    }

    /// <summary>Ordine in cui i predecessori vengono prima dei successori; null se c'e' un ciclo.</summary>
    public static List<long>? TopologicalOrder(IEnumerable<long> ids, IReadOnlyList<PlanLink> links)
    {
        List<long> all = ids.ToList();
        Dictionary<long, int> indegree = all.ToDictionary(i => i, _ => 0);
        Dictionary<long, List<long>> outgoing = new();
        foreach (PlanLink l in links)
        {
            if (!indegree.ContainsKey(l.PredecessorId) || !indegree.ContainsKey(l.SuccessorId))
            {
                continue;
            }

            indegree[l.SuccessorId]++;
            if (!outgoing.TryGetValue(l.PredecessorId, out List<long>? list))
            {
                outgoing[l.PredecessorId] = list = new List<long>();
            }

            list.Add(l.SuccessorId);
        }

        Queue<long> ready = new(all.Where(i => indegree[i] == 0));
        List<long> order = new(all.Count);
        while (ready.Count > 0)
        {
            long id = ready.Dequeue();
            order.Add(id);
            foreach (long next in outgoing.GetValueOrDefault(id) ?? new List<long>())
            {
                if (--indegree[next] == 0)
                {
                    ready.Enqueue(next);
                }
            }
        }

        return order.Count == all.Count ? order : null;
    }

    /// <summary>Collegare <paramref name="predecessor"/> → <paramref name="successor"/> chiuderebbe un ciclo?</summary>
    public static bool WouldCreateCycle(IReadOnlyList<PlanLink> links, long predecessor, long successor)
    {
        if (predecessor == successor)
        {
            return true;
        }

        Dictionary<long, List<long>> outgoing = links.GroupBy(l => l.PredecessorId).ToDictionary(g => g.Key, g => g.Select(l => l.SuccessorId).ToList());
        Stack<long> stack = new();
        HashSet<long> seen = new();
        stack.Push(successor);
        while (stack.Count > 0)
        {
            long id = stack.Pop();
            if (id == predecessor)
            {
                return true;
            }

            if (!seen.Add(id))
            {
                continue;
            }

            foreach (long next in outgoing.GetValueOrDefault(id) ?? new List<long>())
            {
                stack.Push(next);
            }
        }

        return false;
    }

    /// <summary>Le dipendenze non rispettate (successore che comincia troppo presto): frecce rosse nel Gantt.</summary>
    public static HashSet<long> ViolatedLinks(IReadOnlyList<PlanTask> tasks, IReadOnlyList<PlanLink> links, WorkCalendar cal)
    {
        Dictionary<long, PlanTask> byId = tasks.ToDictionary(t => t.Id);
        HashSet<long> violated = new();
        foreach (PlanLink l in links)
        {
            if (byId.TryGetValue(l.PredecessorId, out PlanTask? p) && byId.TryGetValue(l.SuccessorId, out PlanTask? s) &&
                p.Status != PlanStatus.Cancelled && !s.IsPhase && s.Start < EarliestStart(p.End, l.LagDays, cal))
            {
                violated.Add(l.Id);
            }
        }

        return violated;
    }
}
