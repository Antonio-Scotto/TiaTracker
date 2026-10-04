using TiaTracker.Core.Domain;
using TiaTracker.Core.Import;

namespace TiaTracker.Core.Tests;

public class HistoryImportTests
{
    [Fact]
    public void RigaDiTabellaSalvatoNo()
    {
        string[] header = { "Progetto", "PID TIA", "DB", "FB", "Verifica", "Salvato" };
        (ChangeState s, bool notSaved, int? err, int? warn) =
            MarkdownHistoryParser.Evidence("| 0.68 | 40188 | ok, 1 warning | ok, 2 warning | riesporto = patch | **NO** |", header);
        Assert.Equal(ChangeState.Compiled, s);
        Assert.True(notSaved);
        Assert.Equal(0, err);
        Assert.Equal(3, warn);
    }

    [Theory]
    [InlineData("> **STATO 30/09/2026:** tutto **importato sul V0.70** e compilato: **0 errori**, 5 warning. **Progetto NON salvato.**", ChangeState.Compiled, true)]
    [InlineData("> **STATO — 29/09/2026: sorgenti preparati su 0.69, NON importati.**", ChangeState.Planned, false)]
    [InlineData("Importato e salvato sul V0.66, scaricato il 17/09.", ChangeState.Saved, false)]
    [InlineData("Importato sul V0.67, da compilare.", ChangeState.Imported, false)]
    public void RigaDiStato(string line, ChangeState expected, bool notSaved)
    {
        (ChangeState s, bool ns, _, _) = MarkdownHistoryParser.Evidence(line, null);
        Assert.Equal(expected, s);
        Assert.Equal(notSaved, ns);
    }
}
