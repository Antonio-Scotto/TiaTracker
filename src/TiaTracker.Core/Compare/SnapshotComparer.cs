using TiaTracker.Core.Domain;
using TiaTracker.Core.Snapshots;

namespace TiaTracker.Core.Compare;

public enum CompareStatus
{
    Added,
    Removed,
    Modified,
    Moved,
    Renamed,
    Recompiled,
    Reimported,
    Unchanged,
    Unavailable,
}

[Flags]
public enum Facets
{
    None = 0,
    Code = 1,
    Interface = 2,
    Values = 4,
    Comments = 8,
    Meta = 16,
}

/// <summary>Una rete cambiata (LAD/FBD/SCL per rete).</summary>
public sealed record UnitChange(int Index, string? Title, string What);

public sealed class CompareEntry
{
    public string Family { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>Nome nella base per i rinominati.</summary>
    public string? OldName { get; init; }

    public CompareStatus Status { get; set; }

    /// <summary>Gruppo diverso fra base e target (si combina con gli altri stati).</summary>
    public bool Moved { get; set; }

    public Facets Facets { get; set; }

    public SnapshotItem? Base { get; init; }
    public SnapshotItem? Target { get; init; }

    /// <summary>FB o UDT da cui deriva (IDB sotto FB con interfaccia cambiata, DB sotto UDT).</summary>
    public string? ParentName { get; set; }

    public double? Similarity { get; init; }

    public string? Note { get; set; }

    public List<UnitChange> Units { get; } = new();

    public bool IsChange => Status is CompareStatus.Added or CompareStatus.Removed or CompareStatus.Modified
        or CompareStatus.Renamed or CompareStatus.Moved;

    public string StatusText => Status switch
    {
        CompareStatus.Added => "Aggiunto",
        CompareStatus.Removed => "Rimosso",
        CompareStatus.Modified => "Modificato",
        CompareStatus.Moved => "Spostato",
        CompareStatus.Renamed => "Rinominato?",
        CompareStatus.Recompiled => "Solo ricompilato",
        CompareStatus.Reimported => "Reimportato identico",
        CompareStatus.Unchanged => "Invariato",
        _ => "Non confrontabile",
    };

    public string FacetsText => FacetNames(Facets);

    public static string FacetNames(Facets f)
    {
        List<string> parts = new();
        if (f.HasFlag(Facets.Code)) parts.Add("Codice");
        if (f.HasFlag(Facets.Interface)) parts.Add("Interfaccia");
        if (f.HasFlag(Facets.Values)) parts.Add("Valori");
        if (f.HasFlag(Facets.Comments)) parts.Add("Commenti");
        if (f.HasFlag(Facets.Meta)) parts.Add("Meta");
        return string.Join(", ", parts);
    }

    public string Key => Family + "|" + Name.ToUpperInvariant();
}

/// <summary>
/// Confronto di due snapshot dello stesso PLC. Chiave (famiglia, nome);
/// stesso nome in altro gruppo = spostato; sui residui la ricerca dei
/// rinominati; IDB e DB derivati annidati sotto il padre cambiato.
/// </summary>
public static class SnapshotComparer
{
    public const double RenameThreshold = 0.85;

    /// <param name="canonicalLines">Righe canoniche di un elemento, per la somiglianza dei rinominati (null = solo hash).</param>
    public static List<CompareEntry> Compare(
        IReadOnlyList<SnapshotItem> baseItems,
        IReadOnlyList<SnapshotItem> targetItems,
        Func<SnapshotItem, IReadOnlyList<string>?>? canonicalLines = null)
    {
        Dictionary<string, SnapshotItem> b = baseItems.GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.First());
        Dictionary<string, SnapshotItem> t = targetItems.GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.First());
        List<CompareEntry> result = new();

        foreach ((string key, SnapshotItem target) in t)
        {
            if (b.TryGetValue(key, out SnapshotItem? baseItem))
            {
                result.Add(Pair(baseItem, target));
            }
        }

        List<SnapshotItem> removed = b.Where(kv => !t.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
        List<SnapshotItem> added = t.Where(kv => !b.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
        foreach ((SnapshotItem from, SnapshotItem to, double similarity) in RenameDetector.Match(removed, added, canonicalLines, RenameThreshold))
        {
            removed.Remove(from);
            added.Remove(to);
            CompareEntry e = new()
            {
                Family = to.Family,
                Kind = to.Kind,
                Name = to.Name,
                OldName = from.Name,
                Status = CompareStatus.Renamed,
                Moved = !string.Equals(from.GroupPath, to.GroupPath, StringComparison.OrdinalIgnoreCase),
                Base = from,
                Target = to,
                Similarity = similarity,
                Facets = FacetsOf(from, to),
            };
            AddUnitChanges(e);
            result.Add(e);
        }

        result.AddRange(added.Select(i => new CompareEntry
        {
            Family = i.Family, Kind = i.Kind, Name = i.Name, Status = CompareStatus.Added, Target = i,
        }));
        result.AddRange(removed.Select(i => new CompareEntry
        {
            Family = i.Family, Kind = i.Kind, Name = i.Name, Status = CompareStatus.Removed, Base = i,
        }));

        NestDerived(result);
        return result
            .OrderBy(e => Order(e.Status))
            .ThenBy(e => e.Family, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static CompareEntry Pair(SnapshotItem baseItem, SnapshotItem target)
    {
        CompareEntry e = new()
        {
            Family = target.Family,
            Kind = target.Kind,
            Name = target.Name,
            Base = baseItem,
            Target = target,
            Moved = !string.Equals(baseItem.GroupPath, target.GroupPath, StringComparison.OrdinalIgnoreCase),
        };

        if (!baseItem.IsComparable || !target.IsComparable)
        {
            e.Status = CompareStatus.Unavailable;
            SnapshotItem bad = !target.IsComparable ? target : baseItem;
            e.Note = bad.State + (bad.Reason != null ? ": " + bad.Reason : "");
            return e;
        }

        if (baseItem.HAll == target.HAll)
        {
            e.Status = e.Moved ? CompareStatus.Moved
                : !string.Equals(baseItem.CompiledAttr, target.CompiledAttr, StringComparison.Ordinal) ? CompareStatus.Recompiled
                : !string.Equals(baseItem.CodeModifiedAttr, target.CodeModifiedAttr, StringComparison.Ordinal) ? CompareStatus.Reimported
                : CompareStatus.Unchanged;
            return e;
        }

        e.Status = CompareStatus.Modified;
        e.Facets = FacetsOf(baseItem, target);
        AddUnitChanges(e);
        return e;
    }

    public static Facets FacetsOf(SnapshotItem a, SnapshotItem b)
    {
        Facets f = Facets.None;
        if (a.HCode != b.HCode) f |= Facets.Code;
        if (a.HIface != b.HIface) f |= Facets.Interface;
        if (a.HInit != b.HInit) f |= Facets.Values;
        if (a.HText != b.HText) f |= Facets.Comments;
        if (a.HMeta != b.HMeta) f |= Facets.Meta;

        // h_all diverso ma nessuna faccetta: qualcosa fuori dalle parti note.
        if (f == Facets.None && a.HAll != b.HAll)
        {
            f = Facets.Meta;
        }

        return f;
    }

    /// <summary>Reti cambiate, aggiunte o tolte, per LAD/FBD (e SCL a reti).</summary>
    private static void AddUnitChanges(CompareEntry e)
    {
        if (!e.Facets.HasFlag(Facets.Code) && !e.Facets.HasFlag(Facets.Comments))
        {
            return;
        }

        IReadOnlyList<UnitInfo> a = Hasher.ParseUnits(e.Base?.UnitsJson);
        IReadOnlyList<UnitInfo> b = Hasher.ParseUnits(e.Target?.UnitsJson);
        int n = Math.Max(a.Count, b.Count);
        for (int i = 0; i < n; i++)
        {
            UnitInfo? ua = i < a.Count ? a[i] : null;
            UnitInfo? ub = i < b.Count ? b[i] : null;
            if (ua == null)
            {
                e.Units.Add(new UnitChange(i + 1, ub!.Title, "aggiunta"));
            }
            else if (ub == null)
            {
                e.Units.Add(new UnitChange(i + 1, ua.Title, "tolta"));
            }
            else if (ua.HCode != ub.HCode)
            {
                e.Units.Add(new UnitChange(i + 1, ub.Title ?? ua.Title, ua.HText != ub.HText ? "codice e commenti" : "codice"));
            }
            else if (ua.HText != ub.HText)
            {
                e.Units.Add(new UnitChange(i + 1, ub.Title ?? ua.Title, "commenti"));
            }
        }
    }

    /// <summary>
    /// Un IDB cambia quando cambia l'interfaccia del suo FB, un DB tipizzato
    /// quando cambia il suo UDT: si mostrano sotto il padre, non come modifiche a se'.
    /// </summary>
    private static void NestDerived(List<CompareEntry> entries)
    {
        Dictionary<string, CompareEntry> parents = entries
            .Where(e => e.Kind is "FB" or "UDT" && (e.Status == CompareStatus.Added ||
                                                     (e.Status is CompareStatus.Modified or CompareStatus.Renamed &&
                                                      (e.Facets.HasFlag(Facets.Interface) || e.Facets.HasFlag(Facets.Values)))))
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (CompareEntry e in entries)
        {
            if (!e.IsChange || e.Kind is not ("InstanceDB" or "GlobalDB"))
            {
                continue;
            }

            string? of = e.Target?.InstanceOf ?? e.Base?.InstanceOf;
            if (of == null || !parents.ContainsKey(of))
            {
                continue;
            }

            // Solo se l'IDB non ha altro che interfaccia/valori cambiati.
            if (e.Status == CompareStatus.Modified && (e.Facets & ~(Facets.Interface | Facets.Values | Facets.Meta)) != Facets.None)
            {
                continue;
            }

            e.ParentName = parents[of].Name;
        }
    }

    private static int Order(CompareStatus s) => s switch
    {
        CompareStatus.Added => 0,
        CompareStatus.Removed => 1,
        CompareStatus.Modified => 2,
        CompareStatus.Renamed => 3,
        CompareStatus.Moved => 4,
        CompareStatus.Unavailable => 5,
        CompareStatus.Recompiled => 6,
        CompareStatus.Reimported => 7,
        _ => 8,
    };
}
