using TiaTracker.Core.Documents;

namespace TiaTracker.Export;

/// <summary>Misure e colori comuni a DOCX e PDF: stile neutro, A4, Calibri e Consolas.</summary>
internal static class DocLayout
{
    public const string BodyFont = "Calibri";
    public const string MonoFont = "Consolas";

    public const double PageWidthMm = 297;
    public const double PageHeightMm = 210;
    public const double MarginSideMm = 15;
    public const double MarginTopMm = 14;
    public const double MarginBottomMm = 16;
    public const double FooterDistanceMm = 7;

    public const double TitlePt = 16;
    public const double SubtitlePt = 11;
    public const double HeadingPt = 12;
    public const double BodyPt = 9.5;
    public const double CellPt = 8.5;
    public const double MonoPt = 8;
    public const double FooterPt = 7.5;

    // Colori senza "#", come li vuole OpenXml.
    public const string Text = "1F1F1F";
    public const string Muted = "7F7F7F";
    public const string HeaderFill = "F2F2F2";
    public const string GroupFill = "E7E7E7";
    public const string HeaderLine = "808080";
    public const string RowLine = "D9D9D9";

    public static double ContentWidthMm(bool landscape) => (landscape ? PageWidthMm : PageHeightMm) - 2 * MarginSideMm;

    /// <summary>Larghezze delle colonne in proporzione ai pesi; la somma e' esattamente <paramref name="total"/>.</summary>
    public static int[] Widths(IReadOnlyList<DocColumn> columns, int total)
    {
        double sum = columns.Sum(c => Math.Max(0.1, c.Weight));
        int[] w = new int[columns.Count];
        int used = 0;
        for (int i = 0; i < columns.Count; i++)
        {
            w[i] = i == columns.Count - 1 ? total - used : (int)Math.Round(total * Math.Max(0.1, columns[i].Weight) / sum);
            used += w[i];
        }

        return w;
    }

    public static string Cell(DocRow row, int index) => index < row.Cells.Count ? row.Cells[index] ?? "" : "";
}
