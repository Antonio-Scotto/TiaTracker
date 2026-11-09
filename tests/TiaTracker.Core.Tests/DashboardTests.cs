using TiaTracker.Contracts;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Planning;
using TiaTracker.Core.Status;
using TiaTracker.Data;

namespace TiaTracker.Core.Tests;

public class CommessaOverviewTests
{
    [Fact]
    public void QuadroDellaCommessa()
    {
        ProjectVersion work = new() { Id = 2, Label = "V0.70", LastSavedUtc = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc), TiaVersionText = "V21" };
        ProjectVersion loaded = new() { Id = 1, Label = "V0.69" };
        Dictionary<long, ProjectVersion> versions = new() { [1] = loaded, [2] = work };
        List<VersionLoad> loads = new() { new() { VersionId = 1, Plc = "PLC_1", LoadedUtc = new DateTime(2026, 9, 30), ProofSnapshotId = 5 } };
        Snapshot snap = new() { Id = 9, CreatedUtc = new DateTime(2026, 10, 3, 8, 0, 0, DateTimeKind.Utc) };
        List<Change> changes = new()
        {
            new() { Title = "A", Versions = { new ChangeVersion { VersionId = 2, State = ChangeState.Compiled } } },
            new() { Title = "B", Versions = { new ChangeVersion { VersionId = 2, State = ChangeState.Saved } } },
            new() { Title = "C", Draft = true },
        };
        Dictionary<string, int> kinds = new() { ["FB"] = 101, ["OB"] = 3, ["InstanceDB"] = 383, ["Strano"] = 1 };

        CommessaOverview o = CommessaOverview.Build(loads, versions, work, snap, kinds, null, SampleHardware.Build(), changes,
            new List<PlanTask> { Plan.T(1, "FAT", Plan.D(10, 16), Plan.D(10, 16), PlanKind.Milestone) }, Plan.Cal, Plan.D(10, 5));

        PlcLoadCard load = Assert.Single(o.Loads);
        Assert.Equal(("PLC_1", "V0.69", true), (load.Plc, load.VersionLabel, load.HasProof));
        Assert.True(o.SnapshotStale); // salvata dopo lo snapshot
        Assert.Equal("V21", o.TiaVersion);
        CpuCard cpu = Assert.Single(o.Cpus);
        Assert.Equal(("V3.0", "192.168.10.100"), (cpu.Firmware, cpu.Ip));
        Assert.Equal(new[] { "OB", "FB", "DB di istanza" }, o.Blocks.Select(b => b.Label));
        Assert.Equal(487, o.BlocksTotal);
        Assert.Equal((2, 1, 1), (o.Changes, o.ChangesOpenOnWork, o.Drafts));
        Assert.Equal("FAT", o.Plan?.NextMilestone?.Title);
    }
}

public class ActivityRepositoryTests
{
    [Fact]
    public void AttivitaRecentiDaTutteLeStorie()
    {
        using Db db = Db.OpenInMemory();
        Commessa c = new() { Code = "AUT1", Name = "X" };
        new CommessaRepository(db).Insert(c);
        long v = db.Insert("INSERT INTO version (commessa_id, label, sort_key, project_name, first_seen_utc) VALUES ($c, 'V1', 'V1', 'P1', '2026-01-01T00:00:00.000Z')",
            ("$c", c.Id));
        new VersionRepository(db).AddEvent(v, "trovata", "in cartella", new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc));
        PlanRepository plans = new(db);
        PlanTask t = new() { CommessaId = c.Id, Title = "Collaudo", Start = Plan.D(10, 5), End = Plan.D(10, 6) };
        plans.Insert(t, "utente");
        new DocRepository(db).RecordExport(new DocExport
        {
            CommessaId = c.Id, ListKind = "io", GeneratedUtc = DateTime.UtcNow.AddMinutes(1), Formats = "docx,pdf",
        });

        List<ActivityItem> items = new ActivityRepository(db).Recent(c.Id);

        Assert.Equal(new[] { "documenti", "piano", "versione" }, items.Select(i => i.Category));
        Assert.Equal("Lista IO", items[0].Title);
        Assert.Equal("DOCX, PDF generati", items[0].Detail);
        Assert.Equal("attivita' creata", items[1].Detail);
        Assert.Equal("trovata nella cartella", items[2].Detail);
    }
}
