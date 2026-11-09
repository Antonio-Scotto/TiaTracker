using TiaTracker.Core.Domain;
using TiaTracker.Core.Output;
using TiaTracker.Data;

namespace TiaTracker.Core.Tests;

/// <summary>Pulizia automatica: si toglie solo cio' che TiaTracker genera e non rilegge.</summary>
public class HousekeepingTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RestanoGliUltimiExportPerPlcEHardware()
    {
        string[] names =
        {
            "20261001_0900_file_PLC_1", "20261002_0900_attach_PLC_1", "20261003_0900_file_PLC_1", "20261004_0900_file_PLC_1",
            "20261001_0900_file_PLC_2", "20261004_0900_file_PLC_2",
            "20261001_0900_file_hardware", "20261002_0900_attach_hardware", "20261004_0900_file_hardware",
            "Note mie", "vecchi export", "2026_export_a_mano",
        };

        List<string> drop = Retention.ExportFoldersToDelete(names, keep: 2);

        // Per PLC_1 e hardware restano i due piu' recenti; PLC_2 ne ha solo due; nomi non nostri mai.
        Assert.Equal(new[] { "20261001_0900_file_PLC_1", "20261001_0900_file_hardware", "20261002_0900_attach_PLC_1" }, drop.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void ExportProtettiEZeroTengonoTutto()
    {
        string[] names = { "20261001_0900_file_PLC_1", "20261002_0900_file_PLC_1", "20261003_0900_file_PLC_1" };

        Assert.Empty(Retention.ExportFoldersToDelete(names, keep: 0));
        Assert.Empty(Retention.ExportFoldersToDelete(names, keep: 1, new HashSet<string> { "20261001_0900_file_PLC_1", "20261002_0900_file_PLC_1" }));
        Assert.Equal(new[] { "20261001_0900_file_PLC_1" }, Retention.ExportFoldersToDelete(names, keep: 1, new HashSet<string> { "20261002_0900_file_PLC_1" }));
    }

    [Theory]
    [InlineData("V0.68_V0.70_PLC_1_20261004_1015.md", true)]
    [InlineData("V0.68_V0.70_20261004_1015.md", true)]
    [InlineData("Appunti confronto.md", false)]
    [InlineData("V0.68_V0.70_20261004_1015.docx", false)]
    public void ReportDiConfronto(string name, bool ours) => Assert.Equal(ours, Retention.IsCompareReport(name));

    [Fact]
    public void ExportHardwareSuperatiEContenutiOrfani()
    {
        using Db db = Db.OpenInMemory();
        Commessa c = new() { Code = "AUT1", Name = "X" };
        new CommessaRepository(db).Insert(c);
        long v = db.Insert("INSERT INTO version (commessa_id, label, sort_key, project_name, first_seen_utc) VALUES ($c, 'V1', 'V1', 'P1', '2026-01-01T00:00:00.000Z')",
            ("$c", c.Id));
        ContentStore content = new(db);
        HardwareRepository hw = new(db);
        List<long> ids = new();
        for (int i = 0; i < 5; i++)
        {
            string hash = content.Put("{\"export\":" + i + "}", "hardware");
            ids.Add(hw.Insert(new HwSnapshot { VersionId = v, CreatedUtc = Now.AddHours(i), Source = "file", Format = 1, DataHash = hash }));
        }

        // Il piu' vecchio ha generato i documenti: resta.
        new DocRepository(db).RecordExport(new DocExport { CommessaId = c.Id, ListKind = "ip", HwSnapshotId = ids[0], GeneratedUtc = Now, Formats = "pdf" });
        string shared = content.Put("{\"stesso\":1}", "hardware");
        string orphan = content.Put("{\"orfano\":1}", "manifest");

        Assert.Equal(2, hw.Prune(keepPerVersion: 2));
        Assert.Equal(new[] { ids[4], ids[3], ids[0] }, hw.ByVersion(v).Select(h => h.Id));

        (int count, long bytes) = content.DeleteOrphans();
        Assert.Equal(4, count); // i due export tolti, piu' i due contenuti mai usati
        Assert.True(bytes > 0);
        Assert.Null(content.GetText(orphan));
        Assert.Null(content.GetText(shared));
        Assert.NotNull(content.GetText(hw.Get(ids[3])!.DataHash));
        Assert.Equal((0, 0L), content.DeleteOrphans());
    }
}
