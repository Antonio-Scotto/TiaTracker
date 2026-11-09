using TiaTracker.Core.Scanning;

namespace TiaTracker.Core.Tests;

public class VersionNameParserTests
{
    [Theory]
    [InlineData("AUT123456_Demo_V0.68-V21", "V0.68", 0, 68, 21)]
    [InlineData("AUT123456_Demo_V0.70-V21", "V0.70", 0, 70, 21)]
    [InlineData("AUT123456_Demo_V0.57-V21", "V0.57", 0, 57, 21)]
    [InlineData("AUT123456_Demo_V1.2-V18", "V1.2", 1, 2, 18)]
    public void NomeAut(string name, string label, int maj, int min, int tia)
    {
        ParsedName p = VersionNameParser.Parse(name);
        Assert.Equal(NameKind.CodeVersionTia, p.Kind);
        Assert.Equal(label, p.Label);
        Assert.Equal(maj, p.Major);
        Assert.Equal(min, p.Minor);
        Assert.Equal(tia, p.TiaFromName);
        Assert.Equal("AUT123456", p.ProjectCode);
    }

    [Fact]
    public void NomeAutOrdinamentoNumerico()
    {
        // 0.7 < 0.69 < 0.70 non e' un ordine di stringhe: i numeri vanno confrontati come numeri.
        string a = VersionNameParser.Parse("AUT123456_Demo_V0.9-V21").SortKey!;
        string b = VersionNameParser.Parse("AUT123456_Demo_V0.69-V21").SortKey!;
        string c = VersionNameParser.Parse("AUT123456_Demo_V0.70-V21").SortKey!;
        string d = VersionNameParser.Parse("AUT123456_Demo_V1.0-V21").SortKey!;
        Assert.True(string.CompareOrdinal(a, b) < 0);
        Assert.True(string.CompareOrdinal(b, c) < 0);
        Assert.True(string.CompareOrdinal(c, d) < 0);
    }

    [Fact]
    public void Durante()
    {
        ParsedName p = VersionNameParser.Parse("AUT260017_V21");
        Assert.Equal(NameKind.CodeTia, p.Kind);
        Assert.Equal("AUT260017_V21", p.Label);
        Assert.Equal(21, p.TiaFromName);
        Assert.Null(p.SortKey);
    }

    [Theory]
    [InlineData("FRT_AUT220008_28-08-2026", "28-08-2026", null)]
    [InlineData("FRT_AUT220008_30-08-2026_V18", "30-08-2026 V18", 18)]
    [InlineData("FRT_AUT220008_22-04-26x", null, null)]
    public void Hipercast(string name, string? label, int? tia)
    {
        ParsedName p = VersionNameParser.Parse(name);
        if (label == null)
        {
            Assert.Equal(NameKind.Fallback, p.Kind);
            return;
        }

        Assert.Equal(NameKind.Dated, p.Kind);
        Assert.Equal(label, p.Label);
        Assert.Equal(tia, p.TiaFromName);
        Assert.Equal("AUT220008", p.ProjectCode);
    }

    [Fact]
    public void HipercastOrdinePerData()
    {
        string a = VersionNameParser.Parse("FRT_AUT220008_28-08-2026").SortKey!;
        string b = VersionNameParser.Parse("FRT_AUT220008_30-08-2026_V18").SortKey!;
        string c = VersionNameParser.Parse("FRT_AUT220008_31-08-2026_V18").SortKey!;
        Assert.True(string.CompareOrdinal(a, b) < 0);
        Assert.True(string.CompareOrdinal(b, c) < 0);
    }

    [Fact]
    public void RegexPersonalizzata()
    {
        ParsedName p = VersionNameParser.Parse("Impianto_r12_b3", @"^Impianto_r(?<maj>\d+)_b(?<min>\d+)$");
        Assert.Equal(NameKind.Custom, p.Kind);
        Assert.Equal("V12.3", p.Label);
        Assert.Equal(12, p.Major);
    }

    [Theory]
    [InlineData(".ap21", 21, "V21")]
    [InlineData(".ap15_1", 15, "V15.1")]
    [InlineData(".ap18", 18, "V18")]
    [InlineData(".AP19", 19, "V19")]
    public void EstensioneTia(string ext, int major, string text)
    {
        (int Major, string Text)? tia = VersionNameParser.TiaFromExtension(ext);
        Assert.NotNull(tia);
        Assert.Equal(major, tia!.Value.Major);
        Assert.Equal(text, tia.Value.Text);
    }

    [Theory]
    [InlineData(".rar")]
    [InlineData(".apx")]
    [InlineData(".appcert")]
    public void NonProgetto(string ext) => Assert.Null(VersionNameParser.TiaFromExtension(ext));
}
