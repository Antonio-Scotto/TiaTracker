using TiaTracker.Core.Compare;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Reconcile;

namespace TiaTracker.Core.Tests;

/// <summary>Bozza da convalidare dai blocchi cambiati in uno snapshot e non dichiarati.</summary>
public class SnapshotDraftTests
{
    private static readonly ProjectVersion V68 = new() { Id = 1, CommessaId = 7, Label = "V0.68", SortKey = "0000.0068" };
    private static readonly ProjectVersion V70 = new() { Id = 2, CommessaId = 7, Label = "V0.70", SortKey = "0000.0070" };
    private static readonly Dictionary<long, ProjectVersion> Versions = new() { [1] = V68, [2] = V70 };

    private static SnapshotItem I(string name, string kind, string all, string code = "c", string iface = "i", string group = "G",
        string? instanceOf = null) => new()
    {
        Name = name, Kind = kind, Family = "block", GroupPath = group, State = "ok",
        HAll = all, HCode = code, HIface = iface, HInit = "v", HText = "t", HMeta = "m",
        CompiledAttr = "2026-09-30T10:00:00Z", CodeModifiedAttr = "2026-09-30T09:00:00Z", InstanceOf = instanceOf, Language = "SCL",
    };

    private static Snapshot S(long id, long version, int minute, string plc = "PLC_1", string source = SnapshotSources.File, bool partial = false) => new()
    {
        Id = id, VersionId = version, PlcName = plc, Source = source, Partial = partial,
        CreatedUtc = new DateTime(2026, 10, 4, 10, minute, 0, DateTimeKind.Utc),
    };

    /// <summary>Base: nastro dichiarato e modificato, filtro e pompa (con IDB) cambiati senza dichiarazione, un FB nuovo.</summary>
    private static List<CompareEntry> Entries()
    {
        List<SnapshotItem> b = new()
        {
            I("ST020_ConveyorHandle_FB", "FB", "a1", code: "c1"),
            I("ST030_FilterBooking_DB", "GlobalDB", "a2", iface: "i2"),
            I("PUMP_FB", "FB", "a4", iface: "i4"),
            I("PUMP_IDB", "InstanceDB", "a5", iface: "i5", instanceOf: "PUMP_FB"),
            I("UNTOUCHED_FB", "FB", "a9"),
        };
        List<SnapshotItem> t = new()
        {
            I("ST020_ConveyorHandle_FB", "FB", "b1", code: "c1x"),
            I("ST030_FilterBooking_DB", "GlobalDB", "b2", iface: "i2x"),
            I("PUMP_FB", "FB", "b4", iface: "i4x"),
            I("PUMP_IDB", "InstanceDB", "b5", iface: "i5x", instanceOf: "PUMP_FB"),
            I("UNTOUCHED_FB", "FB", "a9"),
            I("SILO_UnloadChain_FB", "FB", "n1"),
        };
        return SnapshotComparer.Compare(b, t);
    }

    private static List<Change> Declared() => new()
    {
        new()
        {
            Id = 10, Title = "Correzione nastro", Blocks = { new ChangeBlock { BlockName = "ST020_ConveyorHandle_FB" } },
            Versions = { new ChangeVersion { VersionId = 2, State = ChangeState.Planned } },
        },
    };

    private static Change? Draft(Snapshot target, Snapshot baseSnap, List<Change> changes, IReadOnlyList<ReconcileLinkInfo>? links = null)
    {
        List<CompareEntry> e = Entries();
        ReconcileResult r = Reconciler.Reconcile(e, changes, V70.Id, baseSnap.VersionId, links ?? Array.Empty<ReconcileLinkInfo>(), target);
        DetectedPlc plc = new(target, baseSnap, "lo snapshot precedente di V0.70", r.Rows, 99);
        return SnapshotDraft.Build(7, V70, new[] { plc }, changes, new DateTime(2026, 10, 4, 18, 0, 0));
    }

    [Fact]
    public void BaseLoSnapshotPrecedenteDellaStessaVersione()
    {
        Snapshot target = S(5, 2, 30);
        List<Snapshot> all = new()
        {
            S(1, 1, 0), S(2, 2, 10), S(3, 2, 20), S(4, 2, 25, plc: "PLC_2"), S(6, 2, 40), target,
        };

        Assert.Equal(3, SnapshotDraft.PickBase(target, all, Versions)!.Id);
    }

    [Fact]
    public void PrimoSnapshotDellaVersioneSiConfrontaConLaPrecedente()
    {
        Snapshot target = S(5, 2, 30);
        List<Snapshot> all = new() { S(1, 1, 0), S(2, 1, 10), S(3, 1, 20, source: SnapshotSources.Export), S(4, 1, 22, partial: true), target };

        // Lo snapshot da cartella di export e quello parziale potrebbero non avere tutti i blocchi.
        Assert.Equal(2, SnapshotDraft.PickBase(target, all, Versions)!.Id);
        Assert.Null(SnapshotDraft.PickBase(S(1, 1, 0), all, Versions));
    }

    [Fact]
    public void BozzaConIBlocchiNonDichiarati()
    {
        Snapshot target = S(5, 2, 30, source: SnapshotSources.Attach);
        target.ProjectModified = true;
        Change d = Draft(target, S(3, 2, 20), Declared())!;

        Assert.True(d.Draft);
        Assert.Equal(7, d.CommessaId);
        Assert.Equal(new DateOnly(2026, 10, 4), d.Date);

        // Il nastro e' dichiarato; l'IDB della pompa segue il suo FB, che e' nella bozza.
        Assert.Equal(new[] { "PUMP_FB", "SILO_UnloadChain_FB", "ST030_FilterBooking_DB" }, d.Blocks.Select(b => b.BlockName).OrderBy(n => n));
        Assert.Equal("nuovo", d.Blocks.Single(b => b.BlockName == "SILO_UnloadChain_FB").Action);
        Assert.Equal("modifica", d.Blocks.Single(b => b.BlockName == "PUMP_FB").Action);
        Assert.Equal("interfaccia", d.Blocks.Single(b => b.BlockName == "ST030_FilterBooking_DB").Note);
        Assert.StartsWith("Blocchi cambiati: ", d.Title);
        Assert.Contains("ST030_FilterBooking_DB: modificato: interfaccia", d.Description);
        Assert.Contains("confrontato con lo snapshot precedente di V0.70", d.Excerpt);

        // Da TIA aperto con modifiche non salvate: Compilata, con il confronto come prova.
        ChangeVersion cv = Assert.Single(d.Versions);
        Assert.Equal(V70.Id, cv.VersionId);
        Assert.Equal(ChangeState.Compiled, cv.State);
        Assert.Equal(99, cv.EvidenceRunId);
    }

    [Fact]
    public void DaFileLaModificaESalvata()
    {
        Change d = Draft(S(5, 2, 30), S(3, 2, 20), Declared())!;
        Assert.Equal(ChangeState.Saved, d.Versions[0].State);
    }

    [Fact]
    public void NienteBozzaSeTuttoEDichiaratoOIgnorato()
    {
        List<Change> changes = Declared();
        changes[0].Blocks.Add(new ChangeBlock { BlockName = "PUMP_FB" });
        changes[0].Blocks.Add(new ChangeBlock { BlockName = "SILO_UnloadChain_FB" });
        ReconcileLinkInfo ignore = new("block", "ST030_FilterBooking_DB", null, ReconcileDecisions.Ignore);

        Assert.Null(Draft(S(5, 2, 30), S(3, 2, 20), changes, new[] { ignore }));
    }

    [Fact]
    public void UnaBozzaDichiaraIBlocchiAlGiroDopo()
    {
        List<Change> changes = Declared();
        Change first = Draft(S(5, 2, 30), S(3, 2, 20), changes)!;
        changes.Add(first);

        // Stesse differenze di nuovo: la bozza (non convalidata) le dichiara gia'.
        Assert.Null(Draft(S(6, 2, 40), S(5, 2, 30), changes));
    }

    [Fact]
    public void IBlocchiPortatiDallaVersionePrecedenteNonVannoNellaBozza()
    {
        List<Change> changes = Declared();
        changes.Add(new Change
        {
            Id = 11, Title = "Pompa (fatta su V0.68)", Blocks = { new ChangeBlock { BlockName = "PUMP_FB" } },
            Versions = { new ChangeVersion { VersionId = 1, State = ChangeState.Saved } },
        });

        Change d = Draft(S(5, 2, 30), S(2, 1, 10), changes)!;

        Assert.DoesNotContain(d.Blocks, b => b.BlockName is "PUMP_FB" or "PUMP_IDB");
        Assert.Contains(d.Blocks, b => b.BlockName == "SILO_UnloadChain_FB");
    }
}
