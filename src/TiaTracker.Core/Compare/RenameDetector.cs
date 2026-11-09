using TiaTracker.Core.Domain;

namespace TiaTracker.Core.Compare;

/// <summary>
/// Fra i blocchi spariti dalla base e quelli comparsi nel target cerca le
/// coppie che sono lo stesso blocco rinominato: stesso codice, interfaccia e
/// valori (certo, se la coppia e' univoca), altrimenti somiglianza di Jaccard sulle righe canoniche
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
        HashSet<SnapshotItem> usedTo = new(), usedFrom = new(), ambiguous = new();

        // 1. Identici a meno del nome, solo se l'abbinamento e' univoco. Due DB sullo stesso UDT o
        //    due FC vuote sono identici fra loro: accoppiarli a caso inventerebbe un rinominato
        //    "certo". Con piu' candidati vale solo lo stesso numero di blocco; gli altri restano
        //    aggiunti e rimossi, e fuori anche dal passo 2, dove sarebbero di nuovo uguali al 100%.
        Dictionary<Signature, List<SnapshotItem>> addedBySignature = added.Where(i => i.IsComparable)
            .GroupBy(SignatureOf)
            .ToDictionary(g => g.Key, g => g.ToList());
        foreach (IGrouping<Signature, SnapshotItem> group in removed.Where(i => i.IsComparable).GroupBy(SignatureOf))
        {
            if (!addedBySignature.TryGetValue(group.Key, out List<SnapshotItem>? tos))
            {
                continue;
            }

            List<SnapshotItem> froms = group.ToList();
            foreach (SnapshotItem from in froms)
            {
                List<SnapshotItem> sameNumber = froms.Count == 1 && tos.Count == 1
                    ? tos
                    : tos.Where(t => from.Number != null && t.Number == from.Number).ToList();
                bool unique = sameNumber.Count == 1 &&
                              (froms.Count == 1 || froms.Count(f => f.Number == from.Number) == 1);
                if (unique && !usedTo.Contains(sameNumber[0]))
                {
                    result.Add((from, sameNumber[0], 1.0));
                    usedTo.Add(sameNumber[0]);
                    usedFrom.Add(from);
                }
            }

            ambiguous.UnionWith(froms.Where(f => !usedFrom.Contains(f)));
            ambiguous.UnionWith(tos.Where(t => !usedTo.Contains(t)));
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
        foreach (SnapshotItem from in removed.Where(i => i.IsComparable && !usedFrom.Contains(i) && !ambiguous.Contains(i)))
        {
            foreach (SnapshotItem to in added.Where(a => a.IsComparable && !usedTo.Contains(a) && !ambiguous.Contains(a) && a.Family == from.Family &&
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

    private readonly record struct Signature(string Family, string Kind, string? Code, string? Iface, string? Init);

    private static Signature SignatureOf(SnapshotItem i) => new(i.Family, i.Kind, i.HCode, i.HIface, i.HInit);

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
