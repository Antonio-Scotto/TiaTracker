using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using PdfSharp.Pdf.IO;
using TiaTracker.Contracts;
using TiaTracker.Core.Documents;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Hardware;
using TiaTracker.Core.Output;
using TiaTracker.Data;
using TiaTracker.Export;

namespace TiaTracker.Core.Tests;

public class DocumentBuilderTests
{
    internal static DocContext Ctx(Dictionary<string, IReadOnlyDictionary<string, string>>? notes = null, IReadOnlyList<DocManualRow>? manual = null) => new()
    {
        Commessa = new Commessa { Id = 1, Code = "AUT123456", Name = "DEMO", Customer = "Cliente" },
        Version = new ProjectVersion
        {
            Id = 5, Label = "V0.70", ProjectName = "AUT123456_Demo_V0.70", TiaVersionText = "V21", State = VersionState.InLavoro,
        },
        HardwareSource = new HwSnapshot
        {
            CreatedUtc = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc), Source = SnapshotSources.Attach, ProjectModified = true,
        },
        GeneratedLocal = new DateTime(2026, 10, 4, 14, 30, 0),
        Notes = notes ?? new Dictionary<string, IReadOnlyDictionary<string, string>>(),
        ManualRows = manual ?? Array.Empty<DocManualRow>(),
    };

    private static Dictionary<string, IReadOnlyDictionary<string, string>> Notes(string kind, string key, string note) => new()
    {
        [kind] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [key] = note },
    };

    [Fact]
    public void ListaIpPerReteEIpInOrdineNumerico()
    {
        List<IpDevice> rows = new()
        {
            new() { Network = "PN/IE_1", Ip = "192.168.10.100", Name = "PLC", Source = IpSources.Tia, TiaKey = "a" },
            new() { Network = "PN/IE_1", Ip = "192.168.10.20", Name = "Inverter", Source = IpSources.Proneta },
            new() { Network = "PN/IE_1", Ip = "192.168.10.3", Name = "ET200", Source = IpSources.Tia, TiaKey = "b", TiaMissing = true },
            new() { Ip = "10.0.0.1", Name = "Router", Notes = "di cantiere" },
        };

        DocDocument d = DocumentBuilders.IpList(Ctx(), rows);
        DocTable t = d.Primary!.Table!;

        Assert.Equal("AUT123456_Lista_IP", d.FileStem);
        Assert.Equal("AUT123456 DEMO · Cliente", d.Subtitle);
        Assert.Equal(new[] { "PN/IE_1", "Senza rete assegnata" }, t.Rows.Where(r => r.Style == DocRowStyle.Group).Select(r => r.Key));
        Assert.Equal(new[] { "192.168.10.3", "192.168.10.20", "192.168.10.100", "10.0.0.1" },
            t.Rows.Where(r => r.Style != DocRowStyle.Group).Select(r => r.Cells[0]));
        Assert.Equal(DocRowStyle.Muted, t.Rows[1].Style);
        Assert.Equal("TIA (non piu' presente)", t.Rows[1].Cells[^1]);
        Assert.Equal("di cantiere", t.Rows[^1].Cells[t.NoteColumn]);
        Assert.Contains(d.Meta, m => m.Label == "Fonte dati" && m.Value.Contains("2 righe da TIA", StringComparison.Ordinal));
        Assert.Contains(d.Meta, m => m.Label == "Generato il" && m.Value == "04/10/2026 14:30");
    }

    [Fact]
    public void ListaHardwareCpuPrimaNoteERigheManuali()
    {
        HardwareExport hw = SampleHardware.Build();
        string diKey = HardwareView.Modules(hw).Single(m => m.Name == "DI 16x24VDC HF_1").Key;
        DocManualRow manual = new()
        {
            Id = 7, ListKind = DocKinds.Hardware, Cells = { ["module"] = "Alimentatore 24V", ["order"] = "6EP1 334-3BA10" },
        };

        DocDocument d = DocumentBuilders.HardwareList(Ctx(Notes(DocKinds.Hardware, diKey, "quadro Q1"), new[] { manual }), hw);
        DocTable t = d.Primary!.Table!;
        List<DocRow> groups = t.Rows.Where(r => r.Style == DocRowStyle.Group).ToList();

        Assert.StartsWith(SampleHardware.Plc + " · ", groups[0].Cells[0]);
        Assert.Contains("192.168.10.100", groups[0].Cells[0]);
        Assert.Equal("Righe aggiunte a mano", groups[^1].Cells[0]);
        DocRow di = t.Rows.Single(r => r.Key == diKey);
        Assert.Equal("quadro Q1", di.Cells[t.NoteColumn]);
        Assert.Equal("%IB10000…10001", di.Cells[5]);
        DocRow last = t.Rows[^1];
        Assert.True(last.IsManual);
        Assert.Equal(7, last.ManualId);
        Assert.Equal("Alimentatore 24V", last.Cells[1]);
        Assert.Equal("6EP1 334-3BA10", last.Cells[3]);

        DocTable bom = d.Sections[1].Table!;
        Assert.DoesNotContain(bom.Rows, r => r.Cells[0]!.Contains('*'));
        Assert.Contains(bom.Rows, r => r.Cells[0] == "6ES7 521-1BH00-0AB0" && r.Cells[2] == "1");
    }

    [Fact]
    public void ListaIoSegnaliPerModuloENoteSulTag()
    {
        HardwareExport hw = SampleHardware.Build();

        DocDocument d = DocumentBuilders.IoList(Ctx(Notes(DocKinds.Io, SampleHardware.Plc + "|emergenza_ok", "fungo quadro")), hw);
        DocTable t = d.Primary!.Table!;
        List<DocRow> data = t.Rows.Where(r => r.Style != DocRowStyle.Group).ToList();

        Assert.Equal(new[] { "%I203.1", "%I1800.0", "%I10000.3", "%Q1800.0", "%I9999.0" }, data.Select(r => r.Cells[0]));
        Assert.Equal("fungo quadro", data[0].Cells[t.NoteColumn]);
        Assert.Equal("MTS_E800-TELEGRAM-1-RX_UDT", data[1].Cells[2]);
        Assert.Equal("Reset quadro", data[2].Cells[3]);
        Assert.Equal("Indirizzi senza modulo nel progetto", t.Rows.Last(r => r.Style == DocRowStyle.Group).Cells[0]);
        Assert.StartsWith("Ingressi · 20A1 - ET200 Main Cabinet · F-DI", t.Rows[0].Cells[0]);

        DocTable areas = d.Sections[1].Table!;
        Assert.Equal(new[] { "F-DI 8x24VDC HF_1", "Telegram 1 (PROFIdrive)_1", "DI 16x24VDC HF_1" }, areas.Rows.Select(r => r.Cells[2]));
        Assert.Equal("1", areas.Rows.Single(r => r.Cells[2] == "DI 16x24VDC HF_1").Cells[^1]);
    }

    [Fact]
    public void SenzaExportHardwareTabelleVuoteConSpiegazione()
    {
        DocDocument d = DocumentBuilders.IoList(Ctx(), null);

        Assert.Empty(d.Primary!.Table!.Rows);
        Assert.Contains("Aggiorna da TIA", d.Primary.Table.EmptyText, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistroConStatiECarichi()
    {
        List<ProjectVersion> versions = new()
        {
            new() { Id = 1, Label = "V0.68", ProjectName = "P_V0.68", State = VersionState.Vecchia },
            new() { Id = 2, Label = "V0.70", ProjectName = "P_V0.70", State = VersionState.Caricata },
        };
        List<VersionLoad> loads = new() { new() { VersionId = 2, Plc = "PLC_1", LoadedUtc = new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc) } };
        Change ch = new()
        {
            Id = 3, Title = "Nuovo allarme", Date = new DateOnly(2026, 9, 29), Blocks = { new ChangeBlock { BlockName = "FB_Allarmi" } },
            Versions = { new ChangeVersion { VersionId = 2, State = ChangeState.Saved } },
        };

        DocDocument d = DocumentBuilders.ChangeRegister(Ctx(), versions, new[] { ch }, loads);

        DocTable v = d.Sections[0].Table!;
        Assert.Equal(DocRowStyle.Muted, v.Rows[0].Style);
        Assert.StartsWith("PLC_1 dal ", v.Rows[1].Cells[3]);
        DocRow c = d.Primary!.Table!.Rows.Single();
        Assert.Equal("29/09/2026", c.Cells[0]);
        Assert.Equal("V0.70: Salvata", c.Cells[4]);
    }
}

public class DocumentRenderTests
{
    internal static DocDocument Big(int rows)
    {
        DocTable t = new() { Columns = { new("ip", "IP", 1, Mono: true), new("name", "Nome", 2), new("note", "Note", 1.5) } };
        t.Rows.Add(DocRow.Group("Rete PN/IE_1 · tanti", "PN/IE_1"));
        for (int i = 0; i < rows; i++)
        {
            t.Rows.Add(new DocRow
            {
                Key = "k" + i,
                Style = i == 1 ? DocRowStyle.Muted : DocRowStyle.Normal,
                Cells = { "192.168.0." + i, "Dispositivo àèìòù " + i, i % 2 == 0 ? "nota; con \"virgolette\"" : null },
            });
        }

        return new DocDocument
        {
            Kind = DocKinds.Ip,
            Title = "Lista IP",
            Subtitle = "AUT1 Prova",
            Meta = { new DocMeta("Commessa", "AUT1 Prova") },
            Sections =
            {
                new DocSection { Title = "Indirizzi", Paragraphs = { "Testo di prova." }, Table = t, IsPrimary = true },
                new DocSection { Title = "Vuota", Table = new DocTable { Columns = { new("a", "A") }, EmptyText = "Niente qui." } },
            },
            FileStem = "AUT1_Lista_IP",
            Footer = "Lista IP · AUT1",
        };
    }

    [Fact]
    public void CsvConBomPuntoEVirgolaEGruppo()
    {
        using MemoryStream ms = new();
        CsvRenderer.Write(Big(3), ms);
        byte[] bytes = ms.ToArray();

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        string[] lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n");
        Assert.Equal("Gruppo;IP;Nome;Note", lines[0]);
        Assert.Equal("PN/IE_1;192.168.0.0;Dispositivo àèìòù 0;\"nota; con \"\"virgolette\"\"\"", lines[1]);
        Assert.Equal("PN/IE_1;192.168.0.1;Dispositivo àèìòù 1;", lines[2]);
        Assert.Equal(5, lines.Length); // intestazione, 3 righe, riga vuota finale
    }

    [Fact]
    public void DocxValidoOrizzontaleConIntestazioneRipetutaENumeriDiPagina()
    {
        using MemoryStream ms = new();
        DocxRenderer.Write(Big(120), ms);
        ms.Position = 0;
        using WordprocessingDocument word = WordprocessingDocument.Open(ms, false);

        Assert.Empty(new OpenXmlValidator().Validate(word).Select(e => e.Description + " @ " + e.Path?.XPath));
        Body body = word.MainDocumentPart!.Document!.Body!;
        PageSize size = body.GetFirstChild<SectionProperties>()!.GetFirstChild<PageSize>()!;
        Assert.True(size.Width!.Value > size.Height!.Value);
        Assert.Equal(PageOrientationValues.Landscape, size.Orient!.Value);
        Table data = body.Elements<Table>().Last();
        Assert.NotNull(data.Elements<TableRow>().First().TableRowProperties!.GetFirstChild<TableHeader>());
        Assert.Equal(1 + 1 + 120, data.Elements<TableRow>().Count());
        string fields = string.Join("|", word.MainDocumentPart.FooterParts.Single().Footer!.Descendants<SimpleField>().Select(f => f.Instruction!.Value));
        Assert.Contains("PAGE", fields, StringComparison.Ordinal);
        Assert.Contains("NUMPAGES", fields, StringComparison.Ordinal);
        Assert.Contains("àèìòù", body.InnerText, StringComparison.Ordinal);
        Assert.Contains("Niente qui.", body.InnerText, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfOrizzontaleSuPiuPagine()
    {
        using MemoryStream ms = new();
        PdfRenderer.Write(Big(150), ms);
        byte[] bytes = ms.ToArray();

        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
        using PdfSharp.Pdf.PdfDocument pdf = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
        Assert.True(pdf.PageCount >= 2, "pagine: " + pdf.PageCount);
        PdfSharp.Pdf.PdfPage page = pdf.Pages[0];
        Assert.True(page.Width.Point > page.Height.Point, page.Width.Point + "x" + page.Height.Point);
    }

    [Theory]
    [InlineData("ST060_DOSAGGIO_SILOS2_VALV_TRAMOGGIA_APR_DIN", 20, "ST060_DOSAGGIO_\nSILOS2_VALV_\nTRAMOGGIA_APR_DIN")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ", 10, "ABCDEFGHIJ\nKLMNOPQRST\nUVWXYZ")]
    [InlineData("uno due ST060_DOSAGGIO_SILOS2", 12, "uno due ST060_\nDOSAGGIO_\nSILOS2")]
    [InlineData("Reset quadro elettrico", 10, "Reset quadro elettrico")]
    public void ParoleLungheSpezzateNelPdf(string text, int max, string expected) =>
        Assert.Equal(expected, PdfRenderer.BreakLongWords(text, max));

    [Fact]
    public void FileApertoSalvatoAccantoSenzaTemporanei()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tt_doc_" + Guid.NewGuid().ToString("N"));
        try
        {
            DocDocument d = Big(5);
            List<DocFileResult> first = DocExporter.Write(d, dir, DocExporter.All);
            Assert.All(first, r => Assert.True(r.Ok && !r.Renamed && File.Exists(r.Path), r.Format + ": " + r.Error));
            Assert.Equal(new[] { "AUT1_Lista_IP.csv", "AUT1_Lista_IP.docx", "AUT1_Lista_IP.pdf" },
                Directory.GetFiles(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));

            string csv = Path.Combine(dir, "AUT1_Lista_IP.csv");
            using (FileStream open = new(csv, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                DocFileResult r = DocExporter.Write(d, dir, new[] { DocFormat.Csv }, new DateTime(2026, 10, 4, 15, 0, 0)).Single();
                Assert.True(r.Ok && r.Renamed);
                Assert.Equal(Path.Combine(dir, "AUT1_Lista_IP_20261004_150000.csv"), r.Path);
            }

            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class DocRepositoryTests
{
    [Fact]
    public void NoteRigheManualiEGenerazioni()
    {
        using Db db = Db.OpenInMemory();
        CommessaRepository commesse = new(db);
        Commessa c = new() { Code = "AUT1", Name = "X" };
        commesse.Insert(c);
        DocRepository repo = new(db);

        repo.SetNote(c.Id, DocKinds.Io, "PLC|TAG_A", " fungo ");
        repo.SetNote(c.Id, DocKinds.Io, "plc|tag_a", "fungo quadro");
        repo.SetNote(c.Id, DocKinds.Hardware, "S1/DI", "Q1");
        Dictionary<string, IReadOnlyDictionary<string, string>> notes = repo.Notes(c.Id);
        Assert.Equal("fungo quadro", Assert.Single(notes[DocKinds.Io]).Value);
        Assert.Equal("fungo quadro", notes[DocKinds.Io]["PLC|tag_A"]);
        repo.SetNote(c.Id, DocKinds.Hardware, "S1/DI", "  ");
        Assert.False(repo.Notes(c.Id).ContainsKey(DocKinds.Hardware));

        long a = repo.AddManualRow(c.Id, DocKinds.Hardware, new Dictionary<string, string> { ["module"] = "Alimentatore", ["note"] = "" });
        long b = repo.AddManualRow(c.Id, DocKinds.Hardware, new Dictionary<string, string> { ["module"] = "Switch" });
        repo.UpdateManualRow(a, new Dictionary<string, string> { ["module"] = "Alimentatore 24V" });
        repo.DeleteManualRow(b);
        DocManualRow row = Assert.Single(repo.ManualRows(c.Id));
        Assert.Equal("Alimentatore 24V", row.Cells["module"]);
        Assert.False(row.Cells.ContainsKey("note"));

        repo.RecordExport(new DocExport
        {
            CommessaId = c.Id, ListKind = DocKinds.Ip, GeneratedUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), Formats = "csv",
        });
        repo.RecordExport(new DocExport
        {
            CommessaId = c.Id, ListKind = DocKinds.Ip, GeneratedUtc = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), Formats = "docx,pdf",
            Files = { "a.docx" },
        });
        DocExport last = repo.LatestExports(c.Id)[DocKinds.Ip];
        Assert.Equal("docx,pdf", last.Formats);
        Assert.Equal(new[] { "a.docx" }, last.Files);
        Assert.Equal(2, repo.Exports(c.Id).Count);
    }

    [Fact]
    public void MigrazioneDocumentiDaV4()
    {
        using Db db = Db.OpenInMemory(4);
        Assert.Equal(4, db.SchemaVersion);

        db.Migrate();

        Assert.Equal(Db.LatestSchemaVersion, db.SchemaVersion);
        Assert.NotNull(db.Scalar("SELECT name FROM sqlite_master WHERE name = 'doc_manual_row'"));
    }

    [Fact]
    public void LeggimiRiscrittoSeDiUnaVersionePrecedente()
    {
        string root = Path.Combine(Path.GetTempPath(), "tt_out_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            string readme = Path.Combine(root, "LEGGIMI.txt");
            File.WriteAllText(readme, "TiaTrackerOut - cartella generata da TiaTracker\r\nRete\\        layout IP\r\n");

            new OutputLayout(root).EnsureCreated();
            Assert.Contains("Elenchi\\", File.ReadAllText(readme), StringComparison.Ordinal);
            Assert.True(Directory.Exists(Path.Combine(root, "Elenchi")));

            File.WriteAllText(readme, "Note mie sulla cartella");
            new OutputLayout(root).EnsureCreated();
            Assert.Equal("Note mie sulla cartella", File.ReadAllText(readme));
            Assert.Equal(Path.Combine(root, "Elenchi", "V0.70"), new OutputLayout(root).ListsDir("V0.70"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
