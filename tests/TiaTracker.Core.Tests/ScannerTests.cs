using TiaTracker.Core.Scanning;

namespace TiaTracker.Core.Tests;

public sealed class FakeTree : IDisposable
{
    public FakeTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "tiatracker_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string File(string rel, string content = "", DateTime? mtimeUtc = null)
    {
        string full = Path.Combine(Root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        if (mtimeUtc.HasValue)
        {
            System.IO.File.SetLastWriteTimeUtc(full, mtimeUtc.Value);
        }

        return full;
    }

    public void Dispose()
    {
        try
        {
            foreach (string f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                System.IO.File.SetAttributes(f, FileAttributes.Normal);
            }

            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
        }
    }
}

public class ScannerTests
{
    [Fact]
    public void CartelleRarEBackup()
    {
        using FakeTree t = new();
        DateTime saved = new(2026, 9, 30, 15, 40, 36, DateTimeKind.Utc);
        t.File(@"AUT123456_Demo_V0.70-V21\AUT123456_Demo_V0.70-V21.ap21", "x");
        t.File(@"AUT123456_Demo_V0.70-V21\System\PEData.plf", "x", saved);
        t.File(@"AUT123456_Demo_V0.70-V21\ProjectInfo.txt", "TiaPortal:\r\n  LastUsed:\r\n  - Version: V21 Update 2\r\n");
        t.File(@"AUT123456_Demo_V0.70-V21.backup\2026-09-30.114113.194\2026-09-30.114113.194.zip", "z");
        t.File(@"AUT123456_Demo_V0.57-V21.rar", "r");
        t.File(@"AUT123456_Demo_V0.66-V21.rar", "r");
        t.File(@"doc\Modifiche.md", "testo");
        t.File(@"documenti.rar", "r");

        List<ScannedVersion> found = ProjectFolderScanner.Scan(t.Root);

        Assert.Equal(new[] { "V0.70", "V0.66", "V0.57" }, found.Select(v => v.Parsed.Label));
        ScannedVersion v070 = found[0];
        Assert.True(v070.HasFolder);
        Assert.True(v070.HasBackup);
        Assert.False(v070.HasRar);
        Assert.Single(v070.BackupZips);
        Assert.Equal(21, v070.TiaMajor);
        Assert.Equal("V21 Update 2", v070.TiaVersionText);
        Assert.Equal(saved, v070.LastSavedUtc);
        Assert.True(found[1].HasRar && !found[1].HasFolder);
    }

    [Fact]
    public void SottoProgettoNonEUnaVersione()
    {
        using FakeTree t = new();
        t.File(@"PLC\FRT_AUT220008_28-08-2026\FRT_AUT220008_28-08-2026.ap15_1", "x");
        t.File(@"PLC\FRT_AUT220008_28-08-2026\ROBOT Roller\ROBOT Roller.ap15_1", "x");
        t.File(@"PLC\FRT_AUT220008_31-08-2026_V18\FRT_AUT220008_31-08-2026_V18.ap18", "x");
        t.File(@"PLC\FRT_AUT220008_31-08-2026_V18.backup\14\2026-09-25.070140.862\2026-09-25.070140.862.zip", "z");
        t.File(@"PLC\FRT_AUT220008_31-08-2026_V18.backup\2026-08-31.012652.713\2026-08-31.012652.713.zip", "z");

        List<ScannedVersion> found = ProjectFolderScanner.Scan(t.Root);

        Assert.Equal(2, found.Count);
        Assert.Equal("31-08-2026 V18", found[0].Parsed.Label);
        Assert.Equal(18, found[0].TiaMajor);
        Assert.Equal(2, found[0].BackupZips.Count);
        Assert.Equal("28-08-2026", found[1].Parsed.Label);
        Assert.Equal("V15.1", found[1].TiaVersionText);
    }

    [Fact]
    public void ScansioneIdempotente()
    {
        using FakeTree t = new();
        t.File(@"AUT260017_V21\AUT260017_V21.ap21", "x");
        t.File(@"AUT260017_V21.rar", "r");
        List<ScannedVersion> a = ProjectFolderScanner.Scan(t.Root);
        List<ScannedVersion> b = ProjectFolderScanner.Scan(t.Root);
        Assert.Single(a);
        Assert.Equal(a[0].SortKey, b[0].SortKey);
        Assert.True(a[0].HasFolder && a[0].HasRar);
        Assert.StartsWith("T", a[0].SortKey);
    }

    [Fact]
    public void ApertoChiusoLockResiduo()
    {
        using FakeTree t = new();
        string folder = Path.Combine(t.Root, "P");
        t.File(@"P\P.ap21", "x");
        Assert.Equal(OpenKind.Closed, OpenStateDetector.Detect(folder, "P", tiaRunning: true, "PC-UFFICIO").Kind);

        t.File(@"P\P.info",
            "<?xml version=\"1.0\"?><InfoFile><User>utente</User><Computer>PC-UFFICIO</Computer><Time>2026-10-04T10:48:17.9314445Z</Time></InfoFile>");
        t.File(@"P\System\~PEData.1", "l");

        OpenState open = OpenStateDetector.Detect(folder, "P", tiaRunning: true, "PC-UFFICIO");
        Assert.Equal(OpenKind.Open, open.Kind);
        Assert.Equal("utente", open.User);
        Assert.Equal(new DateTime(2026, 10, 4, 10, 48, 17, DateTimeKind.Utc), open.SinceUtc!.Value.AddTicks(-(open.SinceUtc.Value.Ticks % TimeSpan.TicksPerSecond)));
        Assert.Equal(DateTimeKind.Utc, open.SinceUtc.Value.Kind);

        Assert.Equal(OpenKind.OpenOtherPc, OpenStateDetector.Detect(folder, "P", true, "ALTRO-PC").Kind);
        Assert.Equal(OpenKind.StaleLock, OpenStateDetector.Detect(folder, "P", false, "PC-UFFICIO").Kind);
    }
}
