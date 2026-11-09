using TiaTracker.Core.Compare;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Reconcile;

namespace TiaTracker.Core.Tests;

public class CompareReconcileTests
{
    private static SnapshotItem I(string name, string kind, string all, string code = "c", string iface = "i", string group = "G",
        string? compiled = "2026-09-30T10:00:00Z", string? instanceOf = null, string family = "block", string? modified = "2026-09-30T09:00:00Z") => new()
    {
        Name = name, Kind = kind, Family = family, GroupPath = group, State = "ok",
        HAll = all, HCode = code, HIface = iface, HInit = "v", HText = "t", HMeta = "m",
        CompiledAttr = compiled, CodeModifiedAttr = modified, InstanceOf = instanceOf, Language = "SCL",
    };

    private static List<CompareEntry> Run(out long v68, out long v70, out List<Change> changes)
    {
        List<SnapshotItem> b = new()
        {
            I("ST020_ConveyorHandle_FB", "FB", "a1", code: "c1"),
            I("ST030_FilterBooking_DB", "GlobalDB", "a2", iface: "i2"),
            I("MOTOR_FC", "FC", "a3"),
            I("PUMP_FB", "FB", "a4", iface: "i4"),
            I("PUMP_IDB", "InstanceDB", "a5", iface: "i5", instanceOf: "PUMP_FB"),
            I("OLD_NAME_FC", "FC", "a6", code: "c6", iface: "i6"),
            I("RECOMPILED_FC", "FC", "a7"),
            I("MOVED_FC", "FC", "a8", group: "A"),
            I("UNTOUCHED_FB", "FB", "a9"),
        };
        List<SnapshotItem> t = new()
        {
            I("ST020_ConveyorHandle_FB", "FB", "b1", code: "c1x"),
            I("ST030_FilterBooking_DB", "GlobalDB", "b2", iface: "i2x"),
            I("MOTOR_FC", "FC", "a3"),
            I("PUMP_FB", "FB", "b4", iface: "i4x"),
            I("PUMP_IDB", "InstanceDB", "b5", iface: "i5x", instanceOf: "PUMP_FB"),
            I("NEW_NAME_FC", "FC", "b6", code: "c6", iface: "i6"),
            I("RECOMPILED_FC", "FC", "a7", compiled: "2026-10-01T10:00:00Z"),
            I("MOVED_FC", "FC", "a8", group: "B"),
            I("UNTOUCHED_FB", "FB", "a9"),
            I("SILO_UnloadChain_FB", "FB", "n1"),
        };

        v68 = 1;
        v70 = 2;
        changes = new List<Change>
        {
            new()
            {
                Id = 10, Title = "Correzione nastro",
                Blocks = { new ChangeBlock { BlockName = "ST020_ConveyorHandle_FB" }, new ChangeBlock { BlockName = "ST030_FilterBooking_DB" } },
                Versions = { new ChangeVersion { VersionId = 2, State = ChangeState.Planned } },
            },
            new()
            {
                Id = 11, Title = "Pompa", Blocks = { new ChangeBlock { BlockName = "PUMP_FB" } },
                Versions = { new ChangeVersion { VersionId = 2, State = ChangeState.Compiled } },
            },
            new()
            {
                Id = 12, Title = "Motore gia' fatto", Blocks = { new ChangeBlock { BlockName = "MOTOR_FC" } },
                Versions = { new ChangeVersion { VersionId = 1, State = ChangeState.Saved }, new ChangeVersion { VersionId = 2, State = ChangeState.Saved } },
            },
            new()
            {
                Id = 13, Title = "Dichiarata e mai fatta", Blocks = { new ChangeBlock { BlockName = "GHOST_FB" } },
                Versions = { new ChangeVersion { VersionId = 2, State = ChangeState.Planned } },
            },
        };

        return SnapshotComparer.Compare(b, t);
    }

    [Fact]
    public void Confronto()
    {
        List<CompareEntry> e = Run(out _, out _, out _);
        CompareEntry Of(string n) => e.Single(x => x.Name == n);

        Assert.Equal(CompareStatus.Modified, Of("ST020_ConveyorHandle_FB").Status);
        Assert.Equal(Facets.Code, Of("ST020_ConveyorHandle_FB").Facets);
        Assert.Equal(Facets.Interface, Of("ST030_FilterBooking_DB").Facets);
        Assert.Equal(CompareStatus.Unchanged, Of("MOTOR_FC").Status);
        Assert.Equal("PUMP_FB", Of("PUMP_IDB").ParentName);
        Assert.Equal(CompareStatus.Renamed, Of("NEW_NAME_FC").Status);
        Assert.Equal("OLD_NAME_FC", Of("NEW_NAME_FC").OldName);
        Assert.Equal(CompareStatus.Recompiled, Of("RECOMPILED_FC").Status);
        Assert.Equal(CompareStatus.Moved, Of("MOVED_FC").Status);
        Assert.Equal(CompareStatus.Added, Of("SILO_UnloadChain_FB").Status);
        Assert.DoesNotContain(e, x => x.Name == "OLD_NAME_FC");
    }

    [Fact]
    public void Riconciliazione()
    {
        List<CompareEntry> e = Run(out long v68, out long v70, out List<Change> changes);
        Snapshot target = new() { Source = SnapshotSources.Attach, ProjectModified = true };
        ReconcileResult r = Reconciler.Reconcile(e, changes, v70, v68, Array.Empty<ReconcileLinkInfo>(), target);
        ReconcileRow Of(string n) => r.Rows.First(x => x.BlockName == n);

        Assert.Equal(ReconcileStatus.Confirmed, Of("ST020_ConveyorHandle_FB").Status);
        Assert.Equal(ReconcileStatus.Confirmed, Of("ST030_FilterBooking_DB").Status);
        Assert.Equal(ReconcileStatus.Confirmed, Of("PUMP_IDB").Status);
        Assert.Equal(ReconcileStatus.AlreadyInBase, Of("MOTOR_FC").Status);
        Assert.Equal(ReconcileStatus.DeclaredNotFound, Of("GHOST_FB").Status);
        Assert.Equal(ReconcileStatus.Undeclared, Of("SILO_UnloadChain_FB").Status);
        Assert.Equal(ReconcileStatus.Undeclared, Of("NEW_NAME_FC").Status);

        // Da TIA aperto con modifiche non salvate: si propone Compilata, non Salvata.
        StateProposal p = Assert.Single(r.Proposals, x => x.Change.Id == 10);
        Assert.Equal(ChangeState.Compiled, p.Proposed);
        Assert.DoesNotContain(r.Proposals, x => x.Change.Id == 11);
    }

    [Fact]
    public void DecisioniRiapplicate()
    {
        List<CompareEntry> e = Run(out long v68, out long v70, out List<Change> changes);
        ReconcileLinkInfo[] links =
        {
            new("block", "SILO_UnloadChain_FB", 11, ReconcileDecisions.Assign),
            new("block", "NEW_NAME_FC", null, ReconcileDecisions.Ignore),
        };
        ReconcileResult r = Reconciler.Reconcile(e, changes, v70, v68, links, new Snapshot { Source = SnapshotSources.File });

        Assert.Equal(ReconcileStatus.Confirmed, r.Rows.First(x => x.BlockName == "SILO_UnloadChain_FB").Status);
        Assert.Equal(ReconcileStatus.Ignored, r.Rows.First(x => x.BlockName == "NEW_NAME_FC").Status);

        // Da file: Salvata, anche per la modifica gia' Compilata.
        Assert.Contains(r.Proposals, x => x.Change.Id == 11 && x.Proposed == ChangeState.Saved);
    }
}
