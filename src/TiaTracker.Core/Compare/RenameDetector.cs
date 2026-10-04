using TiaTracker.Core.Domain;

namespace TiaTracker.Core.Compare;

/// <summary>
/// Fra i blocchi spariti dalla base e quelli comparsi nel target cerca le
/// coppie che sono lo stesso blocco rinominato: stesso codice, interfaccia e
/// valori (certo), altrimenti somiglianza di Jaccard sulle righe canoniche
/// almeno 0,85 con stesso tipo e linguaggio (da confermare a occhio).
/// </summary>
public static class RenameDetector
{
    public static List<(SnapshotItem From, SnapshotItem To, double Similarity)> Match(
        IReadOnlyList<SnapshotItem> removed,
        IReadOnlyList<SnapshotItem> added,
        Func<SnapshotItem, IReadOnlyList<string>?>? canonicalLines,
        double threshold)
    {
        List<(SnapshotItem, SnapshotItem, double)> result = new();
        HashSet<SnapshotItem> usedTo = new(), usedFrom = new();

        // 1. Identici a meno del nome.
        foreach (SnapshotItem from in removed.Where(i => i.IsComparable))
        {
            SnapshotItem? to = added.FirstOrDefault(a => !usedTo.Contains(a) && a.IsComparable && a.Family == from.Family &&
                                                          a.Kind == from.Kind && a.HCode == from.HCode &&
                                                          a.HIface == from.HIface && a.HInit == from.HInit);
            if (to != null)
            {
                result.Add((from, to, 1.0));
                usedTo.Add(to);
                usedFrom.Add(from);
            }
        }

        if (canonicalLines == null)
        {
            return result;
        }

        // 2. Simili: si prende la coppia migliore finche' sopra soglia.
        Dictionary<SnapshotItem, HashSet<string>> sets = new();
        HashSet<string> Lines(SnapshotItem i)
        {
            if (!sets.TryGetValue(i, out HashSet<string>? s))
            {
                // Le righe col nome del blocco cambiano per definizione: fuori.
                s = new HashSet<string>((canonicalLines(i) ?? Array.Empty<string>())
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.Contains(i.Name, StringComparison.Ordinal)), StringComparer.Ordinal);
                sets[i] = s;
            }

            return s;
        }

        List<(SnapshotItem From, SnapshotItem To, double Score)> candidates = new();
        foreach (SnapshotItem from in removed.Where(i => i.IsComparable && !usedFrom.Contains(i)))
        {
            foreach (SnapshotItem to in added.Where(a => a.IsComparable && !usedTo.Contains(a) && a.Family == from.Family &&
                                                         a.Kind == from.Kind &&
                                                         string.Equals(a.Language, from.Language, StringComparison.OrdinalIgnoreCase)))
            {
                double score = Jaccard(Lines(from), Lines(to));
                if (score >= threshold)
                {
                    candidates.Add((from, to, score));
                }
            }
        }

        foreach ((SnapshotItem from, SnapshotItem to, double score) in candidates.OrderByDescending(c => c.Score))
        {
            if (usedFrom.Contains(from) || usedTo.Contains(to))
            {
                continue;
            }

            result.Add((from, to, score));
            usedFrom.Add(from);
            usedTo.Add(to);
        }

        return result;
    }

    public static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0)
        {
            return 0;
        }

        int inter = a.Count(b.Contains);
        int union = a.Count + b.Count - inter;
        return union == 0 ? 0 : (double)inter / union;
    }
}
