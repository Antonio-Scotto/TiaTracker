using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using TiaTracker.Core.Documents;
using L = TiaTracker.Export.DocLayout;

namespace TiaTracker.Export;

/// <summary>
/// DOCX neutro e modificabile in Word: stili Normale e Titolo 1, tabelle a
/// larghezza fissa con riga di intestazione ripetuta, piede con "Pagina X di Y".
/// </summary>
public static class DocxRenderer
{
    private const double TwipsPerMm = 1440 / 25.4;

    public static void Write(DocDocument d, Stream output)
    {
        using WordprocessingDocument word = WordprocessingDocument.Create(output, WordprocessingDocumentType.Document, true);
        word.PackageProperties.Title = d.Title;
        word.PackageProperties.Subject = d.Subtitle;
        word.PackageProperties.Creator = "TiaTracker";
        word.PackageProperties.Created = DateTime.UtcNow;

        MainDocumentPart main = word.AddMainDocumentPart();
        StyleDefinitionsPart styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = Styles();

        FooterPart footerPart = main.AddNewPart<FooterPart>();
        int contentWidth = Twips(L.ContentWidthMm(d.Landscape));
        footerPart.Footer = FooterOf(d.Footer, contentWidth);

        Body body = new();
        body.Append(Para(d.Title, size: L.TitlePt, bold: true, after: 40));
        if (!string.IsNullOrWhiteSpace(d.Subtitle))
        {
            body.Append(Para(d.Subtitle!, size: L.SubtitlePt, color: L.Muted, after: 160));
        }

        if (d.Meta.Count > 0)
        {
            body.Append(MetaTable(d.Meta, contentWidth));
        }

        foreach (DocSection s in d.Sections)
        {
            body.Append(Heading(s.Title));
            foreach (string text in s.Paragraphs)
            {
                body.Append(Para(text, after: 80, keepNext: true));
            }

            if (s.Table == null)
            {
                continue;
            }

            if (s.Table.Rows.Count == 0)
            {
                body.Append(Para(s.Table.EmptyText ?? "Nessun dato.", italic: true, color: L.Muted));
                continue;
            }

            body.Append(DataTable(s.Table, contentWidth));
        }

        // Paragrafo finale vuoto: Word lo vuole dopo una tabella in fondo al documento.
        body.Append(Para("", size: 2));
        body.Append(SectionOf(d.Landscape, main.GetIdOfPart(footerPart)));
        main.Document = new Document(body);
        main.Document.Save();
    }

    // ---------- parti ----------

    private static Styles Styles()
    {
        RunPropertiesBaseStyle baseRun = new(
            new RunFonts { Ascii = L.BodyFont, HighAnsi = L.BodyFont, ComplexScript = L.BodyFont, EastAsia = L.BodyFont },
            new Color { Val = L.Text },
            new FontSize { Val = HalfPoints(L.BodyPt) },
            new FontSizeComplexScript { Val = HalfPoints(L.BodyPt) },
            new Languages { Val = "it-IT" });
        ParagraphPropertiesBaseStyle basePara = new(new SpacingBetweenLines { Before = "0", After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto });

        Style normal = new(new StyleName { Val = "Normal" }, new PrimaryStyle())
        {
            Type = StyleValues.Paragraph,
            StyleId = "Normal",
            Default = true,
        };

        Style heading = new(
            new StyleName { Val = "heading 1" },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new PrimaryStyle(),
            new StyleParagraphProperties(
                new KeepNext(),
                new SpacingBetweenLines { Before = "240", After = "80" },
                new OutlineLevel { Val = 0 }),
            new StyleRunProperties(new Bold(), new FontSize { Val = HalfPoints(L.HeadingPt) }, new FontSizeComplexScript { Val = HalfPoints(L.HeadingPt) }))
        {
            Type = StyleValues.Paragraph,
            StyleId = "Heading1",
        };

        Style table = new(new StyleName { Val = "Normal Table" }, new UIPriority { Val = 99 }, new SemiHidden(), new UnhideWhenUsed(),
            new StyleTableProperties(
                new TableIndentation { Width = 0, Type = TableWidthUnitValues.Dxa },
                new TableCellMarginDefault(
                    new TopMargin { Width = "0", Type = TableWidthUnitValues.Dxa },
                    new TableCellLeftMargin { Width = 108, Type = TableWidthValues.Dxa },
                    new BottomMargin { Width = "0", Type = TableWidthUnitValues.Dxa },
                    new TableCellRightMargin { Width = 108, Type = TableWidthValues.Dxa })))
        {
            Type = StyleValues.Table,
            StyleId = "TableNormal",
            Default = true,
        };

        return new Styles(new DocDefaults(new RunPropertiesDefault(baseRun), new ParagraphPropertiesDefault(basePara)), normal, heading, table);
    }

    private static Footer FooterOf(string text, int contentWidth)
    {
        Paragraph p = new(
            new ParagraphProperties(
                new ParagraphBorders(new TopBorder { Val = BorderValues.Single, Size = 4, Space = 4, Color = L.RowLine }),
                new Tabs(new TabStop { Val = TabStopValues.Right, Position = contentWidth }),
                new SpacingBetweenLines { Before = "0", After = "0" }),
            FooterRun(text),
            new Run(FooterProps(), new TabChar()),
            FooterRun("Pagina "),
            Field("PAGE"),
            FooterRun(" di "),
            Field("NUMPAGES"));
        return new Footer(p);
    }

    private static SimpleField Field(string code) =>
        new(new Run(FooterProps(), new Text("1"))) { Instruction = " " + code + " \\* MERGEFORMAT " };

    private static Run FooterRun(string text) => new(FooterProps(), new Text(text) { Space = SpaceProcessingModeValues.Preserve });

    private static RunProperties FooterProps() =>
        new(new Color { Val = L.Muted }, new FontSize { Val = HalfPoints(L.FooterPt) }, new FontSizeComplexScript { Val = HalfPoints(L.FooterPt) });

    private static SectionProperties SectionOf(bool landscape, string footerId)
    {
        uint w = (uint)Twips(L.PageWidthMm), h = (uint)Twips(L.PageHeightMm);
        return new SectionProperties(
            new FooterReference { Type = HeaderFooterValues.Default, Id = footerId },
            landscape
                ? new PageSize { Width = w, Height = h, Orient = PageOrientationValues.Landscape }
                : new PageSize { Width = h, Height = w },
            new PageMargin
            {
                Top = Twips(L.MarginTopMm),
                Bottom = Twips(L.MarginBottomMm),
                Left = (uint)Twips(L.MarginSideMm),
                Right = (uint)Twips(L.MarginSideMm),
                Header = (uint)Twips(L.FooterDistanceMm),
                Footer = (uint)Twips(L.FooterDistanceMm),
                Gutter = 0,
            });
    }

    // ---------- contenuto ----------

    private static Paragraph Heading(string text) =>
        new(new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }), new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static Paragraph Para(string text, double? size = null, bool bold = false, bool italic = false, string? color = null, int after = 0,
        bool keepNext = false)
    {
        ParagraphProperties pp = new();
        if (keepNext)
        {
            pp.Append(new KeepNext());
        }

        pp.Append(new SpacingBetweenLines { Before = "0", After = after.ToString(CultureInfo.InvariantCulture) });
        return new Paragraph(pp, TextRun(text, RunProps(bold, italic, color, size, mono: false)));
    }

    private static Table MetaTable(IReadOnlyList<DocMeta> meta, int contentWidth)
    {
        int labelWidth = Twips(32);
        Table t = new(
            new TableProperties(
                new TableWidth { Width = contentWidth.ToString(CultureInfo.InvariantCulture), Type = TableWidthUnitValues.Dxa },
                new TableBorders(
                    new TopBorder { Val = BorderValues.Nil }, new LeftBorder { Val = BorderValues.Nil }, new BottomBorder { Val = BorderValues.Nil },
                    new RightBorder { Val = BorderValues.Nil }, new InsideHorizontalBorder { Val = BorderValues.Nil },
                    new InsideVerticalBorder { Val = BorderValues.Nil }),
                new TableLayout { Type = TableLayoutValues.Fixed },
                new TableCellMarginDefault(
                    new TopMargin { Width = "10", Type = TableWidthUnitValues.Dxa },
                    new TableCellLeftMargin { Width = 0, Type = TableWidthValues.Dxa },
                    new BottomMargin { Width = "10", Type = TableWidthUnitValues.Dxa },
                    new TableCellRightMargin { Width = 85, Type = TableWidthValues.Dxa })),
            new TableGrid(new GridColumn { Width = labelWidth.ToString(CultureInfo.InvariantCulture) },
                new GridColumn { Width = (contentWidth - labelWidth).ToString(CultureInfo.InvariantCulture) }));
        foreach (DocMeta m in meta)
        {
            t.Append(new TableRow(
                CellOf(m.Label, labelWidth, RunProps(false, false, L.Muted, L.BodyPt, false), null, JustificationValues.Left),
                CellOf(m.Value, contentWidth - labelWidth, RunProps(false, false, null, L.BodyPt, false), null, JustificationValues.Left)));
        }

        return t;
    }

    private static Table DataTable(DocTable table, int contentWidth)
    {
        int n = table.Columns.Count;
        int[] widths = L.Widths(table.Columns, contentWidth);
        Table t = new(
            new TableProperties(
                new TableWidth { Width = contentWidth.ToString(CultureInfo.InvariantCulture), Type = TableWidthUnitValues.Dxa },
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 4, Color = L.RowLine },
                    new LeftBorder { Val = BorderValues.Nil },
                    new BottomBorder { Val = BorderValues.Single, Size = 4, Color = L.RowLine },
                    new RightBorder { Val = BorderValues.Nil },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 2, Color = L.RowLine },
                    new InsideVerticalBorder { Val = BorderValues.Nil }),
                new TableLayout { Type = TableLayoutValues.Fixed },
                new TableCellMarginDefault(
                    new TopMargin { Width = "25", Type = TableWidthUnitValues.Dxa },
                    new TableCellLeftMargin { Width = 57, Type = TableWidthValues.Dxa },
                    new BottomMargin { Width = "25", Type = TableWidthUnitValues.Dxa },
                    new TableCellRightMargin { Width = 57, Type = TableWidthValues.Dxa })),
            new TableGrid(widths.Select(w => new GridColumn { Width = w.ToString(CultureInfo.InvariantCulture) })));

        // Intestazione ripetuta su ogni pagina (w:tblHeader), con riga piu' marcata sotto.
        TableRow header = new(new TableRowProperties(new CantSplit(), new TableHeader()));
        for (int i = 0; i < n; i++)
        {
            header.Append(CellOf(table.Columns[i].Header, widths[i], RunProps(true, false, null, L.CellPt, false), L.HeaderFill,
                Justify(table.Columns[i].Align), bottomLine: true));
        }

        t.Append(header);
        foreach (DocRow row in table.Rows)
        {
            TableRow r = new(new TableRowProperties(new CantSplit()));
            if (row.Style == DocRowStyle.Group)
            {
                r.Append(CellOf(L.Cell(row, 0), contentWidth, RunProps(true, false, null, L.CellPt, false), L.GroupFill, JustificationValues.Left,
                    span: n, keepNext: true));
                t.Append(r);
                continue;
            }

            string? color = row.Style == DocRowStyle.Muted ? L.Muted : null;
            for (int i = 0; i < n; i++)
            {
                DocColumn c = table.Columns[i];
                r.Append(CellOf(L.Cell(row, i), widths[i], RunProps(false, false, color, c.Mono ? L.MonoPt : L.CellPt, c.Mono), null, Justify(c.Align)));
            }

            t.Append(r);
        }

        return t;
    }

    private static TableCell CellOf(string text, int width, RunProperties props, string? fill, JustificationValues align, int span = 1,
        bool bottomLine = false, bool keepNext = false)
    {
        TableCellProperties tcp = new(new TableCellWidth { Width = width.ToString(CultureInfo.InvariantCulture), Type = TableWidthUnitValues.Dxa });
        if (span > 1)
        {
            tcp.Append(new GridSpan { Val = span });
        }

        if (bottomLine)
        {
            tcp.Append(new TableCellBorders(new BottomBorder { Val = BorderValues.Single, Size = 6, Color = L.HeaderLine }));
        }

        if (fill != null)
        {
            tcp.Append(new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = fill });
        }

        ParagraphProperties pp = new();
        if (keepNext)
        {
            pp.Append(new KeepNext());
        }

        pp.Append(new SpacingBetweenLines { Before = "0", After = "0" });
        pp.Append(new Justification { Val = align });
        Paragraph p = new(pp);
        if (text.Length > 0)
        {
            p.Append(TextRun(text, props));
        }

        return new TableCell(tcp, p);
    }

    private static Run TextRun(string text, RunProperties props) => new(props, new Text(text) { Space = SpaceProcessingModeValues.Preserve });

    private static RunProperties RunProps(bool bold, bool italic, string? color, double? size, bool mono)
    {
        RunProperties rp = new();
        if (mono)
        {
            rp.Append(new RunFonts { Ascii = L.MonoFont, HighAnsi = L.MonoFont, ComplexScript = L.MonoFont });
        }

        if (bold)
        {
            rp.Append(new Bold());
        }

        if (italic)
        {
            rp.Append(new Italic());
        }

        if (color != null)
        {
            rp.Append(new Color { Val = color });
        }

        if (size != null)
        {
            rp.Append(new FontSize { Val = HalfPoints(size.Value) });
            rp.Append(new FontSizeComplexScript { Val = HalfPoints(size.Value) });
        }

        return rp;
    }

    private static JustificationValues Justify(DocAlign a) => a switch
    {
        DocAlign.Center => JustificationValues.Center,
        DocAlign.Right => JustificationValues.Right,
        _ => JustificationValues.Left,
    };

    private static int Twips(double mm) => (int)Math.Round(mm * TwipsPerMm);

    private static string HalfPoints(double pt) => ((int)Math.Round(pt * 2)).ToString(CultureInfo.InvariantCulture);
}
