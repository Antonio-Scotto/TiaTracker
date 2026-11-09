using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using TiaTracker.Core.Documents;
using L = TiaTracker.Export.DocLayout;

namespace TiaTracker.Export;

/// <summary>PDF neutro con MigraDoc: intestazione della tabella ripetuta a ogni pagina, "Pagina X di Y".</summary>
public static class PdfRenderer
{
    private static readonly Color TextColor = Hex(L.Text);
    private static readonly Color MutedColor = Hex(L.Muted);

    public static void Write(DocDocument doc, Stream output)
    {
        WindowsFontResolver.Install();
        PdfDocumentRenderer renderer = new() { Document = Build(doc) };
        renderer.RenderDocument();
        renderer.PdfDocument.Info.Creator = "TiaTracker";
        renderer.PdfDocument.Save(output, false);
    }

    internal static Document Build(DocDocument d)
    {
        Document doc = new() { Culture = CultureInfo.GetCultureInfo("it-IT") };
        doc.Info.Title = d.Title;
        doc.Info.Subject = d.Subtitle ?? "";
        doc.Info.Author = "TiaTracker";

        Style normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = L.BodyFont;
        normal.Font.Size = L.BodyPt;
        normal.Font.Color = TextColor;

        Section section = doc.AddSection();
        // Misure esplicite: con PageFormat.A4 + Orientation.Landscape MigraDoc 6 produce pagine verticali.
        PageSetup ps = doc.DefaultPageSetup.Clone();
        ps.PageWidth = Unit.FromMillimeter(d.Landscape ? L.PageWidthMm : L.PageHeightMm);
        ps.PageHeight = Unit.FromMillimeter(d.Landscape ? L.PageHeightMm : L.PageWidthMm);
        ps.Orientation = Orientation.Portrait;
        ps.LeftMargin = Unit.FromMillimeter(L.MarginSideMm);
        ps.RightMargin = Unit.FromMillimeter(L.MarginSideMm);
        ps.TopMargin = Unit.FromMillimeter(L.MarginTopMm);
        ps.BottomMargin = Unit.FromMillimeter(L.MarginBottomMm);
        ps.FooterDistance = Unit.FromMillimeter(L.FooterDistanceMm);
        section.PageSetup = ps;
        double width = L.ContentWidthMm(d.Landscape);

        Footer(section, d.Footer, width);

        Paragraph title = section.AddParagraph(d.Title);
        title.Format.Font.Size = L.TitlePt;
        title.Format.Font.Bold = true;
        title.Format.SpaceAfter = Unit.FromPoint(2);
        if (!string.IsNullOrWhiteSpace(d.Subtitle))
        {
            Paragraph sub = section.AddParagraph(d.Subtitle);
            sub.Format.Font.Size = L.SubtitlePt;
            sub.Format.Font.Color = MutedColor;
            sub.Format.SpaceAfter = Unit.FromPoint(8);
        }

        MetaTable(section, d.Meta, width);

        foreach (DocSection s in d.Sections)
        {
            Paragraph h = section.AddParagraph(s.Title);
            h.Format.Font.Size = L.HeadingPt;
            h.Format.Font.Bold = true;
            h.Format.SpaceBefore = Unit.FromPoint(12);
            h.Format.SpaceAfter = Unit.FromPoint(4);
            h.Format.KeepWithNext = true;
            h.Format.OutlineLevel = OutlineLevel.Level1;

            foreach (string text in s.Paragraphs)
            {
                Paragraph p = section.AddParagraph(text);
                p.Format.SpaceAfter = Unit.FromPoint(4);
                p.Format.KeepWithNext = true;
            }

            if (s.Table == null)
            {
                continue;
            }

            if (s.Table.Rows.Count == 0)
            {
                Paragraph empty = section.AddParagraph(s.Table.EmptyText ?? "Nessun dato.");
                empty.Format.Font.Italic = true;
                empty.Format.Font.Color = MutedColor;
                continue;
            }

            DataTable(section, s.Table, width);
        }

        return doc;
    }

    private static void Footer(Section section, string text, double widthMm)
    {
        Paragraph f = section.Footers.Primary.AddParagraph();
        f.Format.Font.Size = L.FooterPt;
        f.Format.Font.Color = MutedColor;
        f.Format.Borders.Top.Width = Unit.FromPoint(0.5);
        f.Format.Borders.Top.Color = Hex(L.RowLine);
        f.Format.Borders.DistanceFromTop = Unit.FromPoint(3);
        f.Format.TabStops.AddTabStop(Unit.FromMillimeter(widthMm), TabAlignment.Right);
        f.AddText(text);
        f.AddTab();
        f.AddText("Pagina ");
        f.AddPageField();
        f.AddText(" di ");
        f.AddNumPagesField();
    }

    private static void MetaTable(Section section, IReadOnlyList<DocMeta> meta, double widthMm)
    {
        if (meta.Count == 0)
        {
            return;
        }

        Table t = section.AddTable();
        t.Borders.Visible = false;
        t.LeftPadding = Unit.FromPoint(0);
        t.TopPadding = Unit.FromPoint(1);
        t.BottomPadding = Unit.FromPoint(1);
        t.AddColumn(Unit.FromMillimeter(32));
        t.AddColumn(Unit.FromMillimeter(widthMm - 32));
        foreach (DocMeta m in meta)
        {
            Row r = t.AddRow();
            Paragraph label = r.Cells[0].AddParagraph(m.Label);
            label.Format.Font.Color = MutedColor;
            r.Cells[1].AddParagraph(m.Value);
        }
    }

    private static void DataTable(Section section, DocTable table, double widthMm)
    {
        int n = table.Columns.Count;
        int[] widths = L.Widths(table.Columns, (int)Math.Round(widthMm * 10)); // decimi di mm
        Table t = section.AddTable();
        t.Borders.Visible = false;
        t.Format.Font.Size = L.CellPt;
        t.LeftPadding = Unit.FromPoint(3);
        t.RightPadding = Unit.FromPoint(3);
        t.TopPadding = Unit.FromPoint(1.5);
        t.BottomPadding = Unit.FromPoint(1.5);
        for (int i = 0; i < n; i++)
        {
            Column c = t.AddColumn(Unit.FromMillimeter(widths[i] / 10.0));
            c.Format.Alignment = Align(table.Columns[i].Align);
        }

        Row header = t.AddRow();
        header.HeadingFormat = true;
        header.Shading.Color = Hex(L.HeaderFill);
        header.Format.Font.Bold = true;
        header.Borders.Bottom.Width = Unit.FromPoint(0.75);
        header.Borders.Bottom.Color = Hex(L.HeaderLine);
        for (int i = 0; i < n; i++)
        {
            header.Cells[i].AddParagraph(table.Columns[i].Header);
        }

        foreach (DocRow row in table.Rows)
        {
            Row r = t.AddRow();
            r.Borders.Bottom.Width = Unit.FromPoint(0.25);
            r.Borders.Bottom.Color = Hex(L.RowLine);
            if (row.Style == DocRowStyle.Group)
            {
                r.Shading.Color = Hex(L.GroupFill);
                r.Format.Font.Bold = true;
                r.KeepWith = 1;
                r.Cells[0].MergeRight = n - 1;
                Paragraph g = r.Cells[0].AddParagraph(L.Cell(row, 0));
                g.Format.Alignment = ParagraphAlignment.Left;
                continue;
            }

            if (row.Style == DocRowStyle.Muted)
            {
                r.Format.Font.Color = MutedColor;
            }

            for (int i = 0; i < n; i++)
            {
                string text = L.Cell(row, i);
                if (text.Length == 0)
                {
                    continue;
                }

                bool mono = table.Columns[i].Mono;
                Paragraph p = r.Cells[i].AddParagraph();
                if (mono)
                {
                    p.Format.Font.Name = L.MonoFont;
                    p.Format.Font.Size = L.MonoPt;
                }

                // Larghezza utile in caratteri: cella meno i margini, Consolas 0,55 em, Calibri ~0,56 em per i nomi in maiuscolo.
                double usable = widths[i] / 10.0 * 72 / 25.4 - 6;
                int maxChars = Math.Max(4, (int)(usable / ((mono ? L.MonoPt : L.CellPt) * (mono ? 0.55 : 0.56))));
                string[] lines = BreakLongWords(text, maxChars).Split('\n');
                for (int k = 0; k < lines.Length; k++)
                {
                    if (k > 0)
                    {
                        p.AddLineBreak();
                    }

                    p.AddText(lines[k]);
                }
            }
        }
    }

    /// <summary>
    /// MigraDoc non spezza le parole piu' larghe della cella e le fa sbordare nella
    /// colonna accanto (simboli come ST060_DOSAGGIO_SILOS2_VALV_TRAMOGGIA_APR_DIN):
    /// le spezziamo noi dopo "_", ".", "-" o "/", a forza se non ce ne sono.
    /// </summary>
    internal static string BreakLongWords(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        System.Text.StringBuilder sb = new(text.Length + 8);
        string[] words = text.Split(' ');
        for (int w = 0; w < words.Length; w++)
        {
            if (w > 0)
            {
                sb.Append(' ');
            }

            string word = words[w];
            while (word.Length > maxChars)
            {
                int cut = word.LastIndexOfAny(new[] { '_', '.', '-', '/' }, maxChars - 1, maxChars - 1) + 1;
                if (cut < maxChars / 3)
                {
                    cut = maxChars;
                }

                sb.Append(word, 0, cut).Append('\n');
                word = word[cut..];
            }

            sb.Append(word);
        }

        return sb.ToString();
    }

    private static ParagraphAlignment Align(DocAlign a) => a switch
    {
        DocAlign.Center => ParagraphAlignment.Center,
        DocAlign.Right => ParagraphAlignment.Right,
        _ => ParagraphAlignment.Left,
    };

    private static Color Hex(string rgb) => new(
        byte.Parse(rgb.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(rgb.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(rgb.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
}
