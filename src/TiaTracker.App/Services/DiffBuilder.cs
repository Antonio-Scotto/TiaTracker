using DiffPlex;
using DiffPlex.Model;
using TiaTracker.Core.Snapshots;

namespace TiaTracker.App.Services;

public enum DiffKind
{
    Same,
    Changed,
    Deleted,
    Inserted,
    Gap,
}

public sealed record DiffRow(int? LeftNo, string LeftText, int? RightNo, string RightText, DiffKind Kind);

/// <summary>
/// Diff affiancato: DiffPlex confronta le righe normalizzate (spazi e
/// indentazione non contano), a video vanno le righe originali.
/// </summary>
public static class DiffBuilder
{
    public static List<DiffRow> Build(IReadOnlyList<SourceLine> left, IReadOnlyList<SourceLine> right)
    {
        string a = string.Join("\n", left.Select(l => l.Normalized));
        string b = string.Join("\n", right.Select(l => l.Normalized));
        DiffResult diff = Differ.Instance.CreateCustomDiffs(a, b, false, s => s.Length == 0 ? Array.Empty<string>() : s.Split('\n'));

        List<DiffRow> rows = new(Math.Max(left.Count, right.Count) + 16);
        int ia = 0, ib = 0;
        foreach (DiffBlock block in diff.DiffBlocks)
        {
            while (ia < block.DeleteStartA && ib < block.InsertStartB)
            {
                rows.Add(Same(left, right, ia++, ib++));
            }

            int common = Math.Min(block.DeleteCountA, block.InsertCountB);
            for (int i = 0; i < common; i++)
            {
                rows.Add(new DiffRow(ia + 1, left[ia].Original, ib + 1, right[ib].Original, DiffKind.Changed));
                ia++;
                ib++;
            }

            for (int i = common; i < block.DeleteCountA; i++)
            {
                rows.Add(new DiffRow(ia + 1, left[ia].Original, null, "", DiffKind.Deleted));
                ia++;
            }

            for (int i = common; i < block.InsertCountB; i++)
            {
                rows.Add(new DiffRow(null, "", ib + 1, right[ib].Original, DiffKind.Inserted));
                ib++;
            }
        }

        while (ia < left.Count && ib < right.Count)
        {
            rows.Add(Same(left, right, ia++, ib++));
        }

        while (ia < left.Count)
        {
            rows.Add(new DiffRow(ia + 1, left[ia].Original, null, "", DiffKind.Deleted));
            ia++;
        }

        while (ib < right.Count)
        {
            rows.Add(new DiffRow(null, "", ib + 1, right[ib].Original, DiffKind.Inserted));
            ib++;
        }

        return rows;
    }

    /// <summary>Solo le differenze con qualche riga di contesto; i salti diventano una riga "...".</summary>
    public static List<DiffRow> OnlyChanges(IReadOnlyList<DiffRow> rows, int context = 3)
    {
        bool[] keep = new bool[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Kind is DiffKind.Same)
            {
                continue;
            }

            for (int k = Math.Max(0, i - context); k <= Math.Min(rows.Count - 1, i + context); k++)
            {
                keep[k] = true;
            }
        }

        List<DiffRow> result = new();
        bool gap = false;
        for (int i = 0; i < rows.Count; i++)
        {
            if (keep[i])
            {
                result.Add(rows[i]);
                gap = false;
            }
            else if (!gap)
            {
                result.Add(new DiffRow(null, "...", null, "...", DiffKind.Gap));
                gap = true;
            }
        }

        return result;
    }

    private static DiffRow Same(IReadOnlyList<SourceLine> l, IReadOnlyList<SourceLine> r, int a, int b) =>
        new(a + 1, l[a].Original, b + 1, r[b].Original, DiffKind.Same);
}
