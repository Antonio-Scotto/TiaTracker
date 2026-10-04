using TiaTracker.Core.Domain;
using TiaTracker.Core.Output;

namespace TiaTracker.Core.Tests;

public class OutputTests
{
    [Theory]
    [InlineData("AUT123456_Demo_V0.70-V21", "AUT123456_Demo_V0.71-V21")]
    [InlineData("AUT123456_Demo_V0.09-V21", "AUT123456_Demo_V0.10-V21")]
    [InlineData("AUT123456_Demo_V1.9-V18", "AUT123456_Demo_V1.10-V18")]
    [InlineData("FRT_AUT220008_31-08-2026_V18", "FRT_AUT220008_04-10-2026_V18")]
    [InlineData("FRT_AUT220008_28-08-2026", "FRT_AUT220008_04-10-2026")]
    [InlineData("AUT260017_V21", "AUT260017_V21_20261004")]
    public void NomeVersioneSuccessiva(string from, string expected)
    {
        Assert.Equal(expected, VersionNaming.Next(from, new[] { from }, new DateTime(2026, 10, 4)));
    }

    [Fact]
    public void NomeGiaPresenteSiSalta()
    {
        string next = VersionNaming.Next("AUT123456_Demo_V0.70-V21",
            new[] { "AUT123456_Demo_V0.70-V21", "AUT123456_Demo_V0.71-V21" }, DateTime.Today);
        Assert.Equal("AUT123456_Demo_V0.72-V21", next);
    }

    [Fact]
    public void IncollaDaGoogleFogliConIntestazione()
    {
        const string tsv = "Dispositivo\tIndirizzo IP\tSubnet mask\tNote\r\n" +
                           "PLC_1 10A1\t192.168.10.100\t255.255.255.0\tCPU 1515F\r\n" +
                           "plc_linea2\t192.168.10.101\t255.255.255.0\t\r\n";
        List<IpDevice> d = IpTableFormat.Parse(tsv);
        Assert.Equal(2, d.Count);
        Assert.Equal("PLC_1 10A1", d[0].Name);
        Assert.Equal("192.168.10.100", d[0].Ip);
        Assert.Equal("CPU 1515F", d[0].Notes);
        Assert.Null(d[1].Notes);
    }

    [Fact]
    public void IncollaSenzaIntestazioneVaPerPosizione()
    {
        List<IpDevice> d = IpTableFormat.Parse("PROFINET\t192.168.0.10\t255.255.255.0\t\tInverter 26E1\tFR-E800");
        IpDevice one = Assert.Single(d);
        Assert.Equal("PROFINET", one.Network);
        Assert.Equal("Inverter 26E1", one.Name);
        Assert.Equal("FR-E800", one.Kind);
    }

    [Fact]
    public void CsvAndataERitorno()
    {
        List<IpDevice> src = new()
        {
            new IpDevice { Ip = "10.0.0.1", Name = "HMI; pannello", Notes = "con \"virgolette\"" },
            new IpDevice { Ip = "10.0.0.2", Name = "Switch" },
        };
        List<IpDevice> back = IpTableFormat.Parse(IpTableFormat.ToCsv(src));
        Assert.Equal(2, back.Count);
        Assert.Equal("HMI; pannello", back[0].Name);
        Assert.Equal("con \"virgolette\"", back[0].Notes);
    }

    [Theory]
    [InlineData("192.168.10.100", true)]
    [InlineData("192.168.10.256", false)]
    [InlineData("192.168.10", false)]
    [InlineData("fe80::1", false)]
    public void IpValido(string ip, bool ok) => Assert.Equal(ok, IpTableFormat.IsValidIp(ip));

    [Fact]
    public void OrdinamentoNumerico() =>
        Assert.True(string.CompareOrdinal(IpTableFormat.SortKey("192.168.0.9"), IpTableFormat.SortKey("192.168.0.10")) < 0);

    [Fact]
    public void CartellaPredefinita()
    {
        OutputLayout? l = OutputLayout.For(new Commessa(), new[] { new ScanRoot { Path = @"C:\Commesse\Demo\PLC\TIA21" } });
        Assert.Equal(@"C:\Commesse\Demo\PLC\TIA21\TiaTrackerOut", l!.Root);
        Assert.StartsWith(@"C:\Commesse\Demo\PLC\TIA21\TiaTrackerOut\Snapshot\V0.70\", l.SnapshotDir("V0.70", DateTime.UtcNow, "attach", "PLC_1"));
    }
}
