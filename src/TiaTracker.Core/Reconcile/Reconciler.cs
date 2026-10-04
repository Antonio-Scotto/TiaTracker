using TiaTracker.Core.Compare;
using TiaTracker.Core.Domain;

namespace TiaTracker.Core.Reconcile;

public enum ReconcileStatus
{
    /// <summary>Dichiarato in una modifica e cambiato davvero.</summary>
    Confirmed,

    /// <summary>Dichiarato ma non cambiato fra base e target (o assente).</summary>
    DeclaredNotFound,

    /// <summary>Dichiarato ma gia' identico nella base: la modifica c'era gia'.</summary>
    AlreadyInBase,

    /// <summary>Cambiato ma nessuna modifica lo dichiara.</summary>
    Undeclared,

    /// <summary>Cambiato e dichiarato da piu' modifiche.</summary>
    Ambiguous,

    /// <summary>Cambiato, l'utente ha deciso di non tracciarlo.</summary>
    Ignored,
}

public static class ReconcileDecisions
{
    public const string Assign = "assign";
    public const string Ignore = "ignore";
}

/// <summary>Decisione dell'utente salvata in reconcile_link.</summary>
public sealed record ReconcileLinkInfo(string Family, string BlockName, long? ChangeId, string Decision);

public sealed class ReconcileRow
{
    public ReconcileStatus Status { get; init; }
    public string BlockName { get; init; } = "";
    public string? Family { get; init; }
    public List<Change> Changes { get; init; } = new();
    public CompareEntry? Entry { get; init; }
    public string? Note { get; init; }

    public string StatusText => Status switch
    {
        ReconcileStatus.Confirmed => "Confermata",
        ReconcileStatus.DeclaredNotFound => "Dichiarata non trovata",
        ReconcileStatus.AlreadyInBase => "Gia' nella base",
        ReconcileStatus.Undeclared => "Non dichiarata",
        ReconcileStatus.Ambiguous => "Ambigua",
        _ => "Ignorata",
    };

    public string ChangesText => string.Join("; ", Changes.Select(c => c.Title));
}

/// <summary>Stato proposto per una modifica, da confermare a mano.</summary>
public sealed record StateProposal(Change Change, ChangeState? Current, ChangeState Proposed, string Reason);

public sealed record ReconcileResult(List<ReconcileRow> Rows, List<StateProposal> Proposals);

/// <summary>
/// Incrocia cio' che le modifiche dichiarano (blocchi toccati) con cio' che
/// il confronto ha trovato davvero fra base e target.
/// </summary>
public static class Reconciler
{
    public static ReconcileResult Reconcile(
        IReadOnlyList<CompareEntry> entries,
        IReadOnlyList<Change> changes,
        long targetVersionId,
        long? baseVersionId,
        IReadOnlyList<ReconcileLinkInfo> links,
        Snapshot? targetSnapshot)
    {
        // Le modifiche della versione target, tranne le scartate.
        List<Change> active = changes
            .Where(c => c.Versions.Any(v => v.VersionId == targetVersionId && v.State != ChangeState.Dropped))
            .ToList();

        Dictionary<string, List<Change>> declared = new(StringComparer.OrdinalIgnoreCase);
        void Declare(string name, Change c)
        {
            if (!declared.TryGetValue(name, out List<Change>? list))
            {
                declared[name] = list = new List<Change>();
            }

            if (!list.Contains(c))
            {
                list.Add(c);
            }
        }

        foreach (Change c in active)
        {
            foreach (ChangeBlock b in c.Blocks)
            {
                Declare(b.BlockName, c);
            }
        }

        // Assegnazioni manuali di blocchi non dichiarati.
        foreach (ReconcileLinkInfo link in links.Where(l => l.Decision == ReconcileDecisions.Assign && l.ChangeId != null))
        {
            Change? c = active.FirstOrDefault(x => x.Id == link.ChangeId);
            if (c != null)
            {
                Declare(link.BlockName, c);
            }
        }

        HashSet<string> ignored = new(links.Where(l => l.Decision == ReconcileDecisions.Ignore).Select(l => l.BlockName),
            StringComparer.OrdinalIgnoreCase);

        List<ReconcileRow> rows = new();
        HashSet<string> matched = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, CompareEntry> changedByName = new(StringComparer.OrdinalIgnoreCase);

        foreach (CompareEntry e in entries.Where(e => e.IsChange))
        {
            changedByName.TryAdd(e.Name, e);
            List<Change>? who = Lookup(declared, e.Name) ?? (e.OldName != null ? Lookup(declared, e.OldName) : null);

            if (who == null && e.ParentName != null && declared.ContainsKey(e.ParentName))
            {
                // IDB o DB derivato da un FB/UDT dichiarato: e' una conseguenza.
                matched.Add(e.Name);
                rows.Add(new ReconcileRow
                {
                    Status = ReconcileStatus.Confirmed, BlockName = e.Name, Family = e.Family, Entry = e,
                    Changes = declared[e.ParentName], Note = "derivato da " + e.ParentName,
                });
                continue;
            }

            if (who == null)
            {
                rows.Add(new ReconcileRow
                {
                    Status = ignored.Contains(e.Name) ? ReconcileStatus.Ignored : ReconcileStatus.Undeclared,
                    BlockName = e.Name, Family = e.Family, Entry = e,
                    Note = e.ParentName != null ? "derivato da " + e.ParentName : null,
                });
                continue;
            }

            matched.Add(e.Name);
            if (e.OldName != null)
            {
                matched.Add(e.OldName);
            }

            rows.Add(new ReconcileRow
            {
                Status = who.Count > 1 ? ReconcileStatus.Ambiguous : ReconcileStatus.Confirmed,
                BlockName = e.Name, Family = e.Family, Entry = e, Changes = who,
                Note = e.OldName != null ? "rinominato da " + e.OldName : null,
            });
        }

        // Dichiarati ma non cambiati.
        Dictionary<string, CompareEntry> all = entries
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach ((string name, List<Change> who) in declared)
        {
            if (matched.Contains(name))
            {
                continue;
            }

            all.TryGetValue(name, out CompareEntry? entry);
            foreach (Change c in who)
            {
                bool inBase = baseVersionId != null &&
                              c.Versions.Any(v => v.VersionId == baseVersionId && v.State is ChangeState.Imported or ChangeState.Compiled or ChangeState.Saved);
                string note = entry == null ? "assente nello snapshot"
                    : entry.Status == CompareStatus.Unavailable ? "non confrontabile: " + entry.Note
                    : "presente ma " + entry.StatusText.ToLowerInvariant();
                rows.Add(new ReconcileRow
                {
                    Status = entry != null && entry.Status != CompareStatus.Unavailable && inBase ? ReconcileStatus.AlreadyInBase : ReconcileStatus.DeclaredNotFound,
                    BlockName = name,
                    Family = entry?.Family,
                    Entry = entry,
                    Changes = new List<Change> { c },
                    Note = note,
                });
            }
        }

        List<StateProposal> proposals = Propose(active, rows, targetVersionId, targetSnapshot);
        return new ReconcileResult(
            rows.OrderBy(r => Order(r.Status)).ThenBy(r => r.BlockName, StringComparer.OrdinalIgnoreCase).ToList(),
            proposals);
    }

    /// <summary>
    /// Modifica con tutti i blocchi confermati: da file = Salvata; da TIA
    /// aperto con modifiche non salvate = Compilata (non salvata); da TIA aperto
    /// senza modifiche pendenti = Salvata. Mai verso il basso, mai da sola.
    /// </summary>
    private static List<StateProposal> Propose(List<Change> active, List<ReconcileRow> rows, long targetVersionId, Snapshot? snapshot)
    {
        List<StateProposal> result = new();
        if (snapshot == null)
        {
            return result;
        }

        bool unsaved = snapshot.Source == SnapshotSources.Attach && snapshot.ProjectModified == true;
        ChangeState proposed = unsaved ? ChangeState.Compiled : ChangeState.Saved;
        string reason = unsaved
            ? "blocchi presenti nello snapshot da TIA aperto con modifiche NON salvate"
            : snapshot.Source == SnapshotSources.Attach
                ? "blocchi presenti nello snapshot da TIA aperto, progetto salvato"
                : "blocchi presenti nel progetto salvato su disco";

        foreach (Change c in active)
        {
            List<ReconcileRow> mine = rows.Where(r => r.Changes.Contains(c)).ToList();
            if (mine.Count == 0 || c.Blocks.Count == 0)
            {
                continue;
            }

            bool allFound = mine.All(r => r.Status is ReconcileStatus.Confirmed or ReconcileStatus.AlreadyInBase or ReconcileStatus.Ambiguous);
            if (!allFound)
            {
                continue;
            }

            ChangeState? current = c.Versions.FirstOrDefault(v => v.VersionId == targetVersionId)?.State;
            if (current == null || Rank(current.Value) < Rank(proposed))
            {
                result.Add(new StateProposal(c, current, proposed, reason));
            }
        }

        return result;
    }

    private static int Rank(ChangeState s) => s switch
    {
        ChangeState.Planned => 0,
        ChangeState.Imported => 1,
        ChangeState.Compiled => 2,
        ChangeState.Saved => 3,
        _ => -1,
    };

    private static List<Change>? Lookup(Dictionary<string, List<Change>> d, string name) =>
        d.TryGetValue(name, out List<Change>? v) ? v : null;

    private static int Order(ReconcileStatus s) => s switch
    {
        ReconcileStatus.Undeclared => 0,
        ReconcileStatus.DeclaredNotFound => 1,
        ReconcileStatus.Ambiguous => 2,
        ReconcileStatus.Confirmed => 3,
        ReconcileStatus.AlreadyInBase => 4,
        _ => 5,
    };
}
