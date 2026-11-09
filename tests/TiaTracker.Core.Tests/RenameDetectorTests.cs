using TiaTracker.Core.Compare;
using TiaTracker.Core.Domain;

namespace TiaTracker.Core.Tests;

public class RenameDetectorTests
{
    private static SnapshotItem I(string name, string kind = "GlobalDB", string code = "c", string iface = "udt", int? number = null) => new()
    {
        Name = name, Kind = kind, Family = "block", State = "ok", Language = "DB",
        HAll = name, HCode = code, HIface = iface, HInit = "v", HText = "t", HMeta = "m", Number = number,
    };

    /// <summary>Stesse righe per tutti: nel passo 2 sarebbero simili al 100%.</summary>
    private static IReadOnlyList<string> SameLines(SnapshotItem _) => new[] { "STRUCT", "Valore : Int;", "END_STRUCT" };

    [Fact]
    public void CoppiaUnivocaECerta()
    {
        SnapshotItem from = I("DB_Vecchio"), to = I("DB_Nuovo");
        var m = RenameDetector.Match(new[] { from }, new[] { to }, null, 0.85);
        Assert.Equal((from, to, 1.0), Assert.Single(m));
    }

    [Fact]
    public void BlocchiGemelliNonSiAccoppianoACaso()
    {
        // Due DB sullo stesso UDT spariti e due comparsi: quale e' diventato quale non si sa.
        SnapshotItem[] removed = { I("DB_Linea1"), I("DB_Linea2") };
        SnapshotItem[] added = { I("DB_LineaA"), I("DB_LineaB") };
        Assert.Empty(RenameDetector.Match(removed, added, SameLines, 0.85));
    }

    [Fact]
    public void FraGemelliDecideIlNumeroDelBlocco()
    {
        SnapshotItem[] removed = { I("DB_Linea1", number: 101), I("DB_Linea2", number: 102) };
        SnapshotItem[] added = { I("DB_LineaB", number: 102), I("DB_LineaA", number: 101) };
        var m = RenameDetector.Match(removed, added, SameLines, 0.85);
        Assert.Equal(2, m.Count);
        Assert.Contains(m, x => x.From.Name == "DB_Linea1" && x.To.Name == "DB_LineaA");
        Assert.Contains(m, x => x.From.Name == "DB_Linea2" && x.To.Name == "DB_LineaB");
    }

    [Fact]
    public void UnoSparitoEDueGemelliComparsiRestaAggiunto()
    {
        SnapshotItem[] removed = { I("FC_Vuota", kind: "FC", code: "vuoto", iface: "nessuna") };
        SnapshotItem[] added = { I("FC_A", kind: "FC", code: "vuoto", iface: "nessuna"), I("FC_B", kind: "FC", code: "vuoto", iface: "nessuna") };
        Assert.Empty(RenameDetector.Match(removed, added, SameLines, 0.85));
    }

    [Fact]
    public void IGemelliNonToglionoIlPassoSimileAgliAltri()
    {
        // Un FB rinominato e ritoccato (passo 2) accanto a due DB gemelli ambigui.
        SnapshotItem oldFb = I("FB_Pompa", kind: "FB", code: "c1", iface: "i1");
        SnapshotItem newFb = I("FB_PompaNuova", kind: "FB", code: "c2", iface: "i1");
        SnapshotItem[] removed = { oldFb, I("DB_Linea1"), I("DB_Linea2") };
        SnapshotItem[] added = { newFb, I("DB_LineaA"), I("DB_LineaB") };
        var m = RenameDetector.Match(removed, added, SameLines, 0.85);
        var only = Assert.Single(m);
        Assert.Same(oldFb, only.From);
        Assert.Same(newFb, only.To);
    }
}
