using TiaTracker.Core.Documents;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Planning;
using TiaTracker.Data;

namespace TiaTracker.Core.Tests;

internal static class Plan
{
    public static readonly WorkCalendar Cal = WorkCalendar.Default;

    /// <summary>Ottobre 2026: lunedi' 5, venerdi' 9, lunedi' 12...</summary>
    public static DateOnly D(int month, int day) => new(2026, month, day);

    public static PlanTask T(long id, string title, DateOnly start, DateOnly end, PlanKind kind = PlanKind.Task) =>
        new() { Id = id, CommessaId = 1, Title = title, Start = start, End = end, Kind = kind };

    public static Dictionary<long, (DateOnly Start, DateOnly End)> Edit(long id, DateOnly start, DateOnly end) => new() { [id] = (start, end) };
}

public class WorkCalendarTests
{
    [Fact]
    public void PasquaEFesteNazionali()
    {
        Assert.Equal(new DateOnly(2026, 4, 5), WorkCalendar.Easter(2026));
        Assert.Equal(new DateOnly(2025, 4, 20), WorkCalendar.Easter(2025));
        Assert.False(Plan.Cal.IsWorkingDay(Plan.D(4, 6)));   // Pasquetta
        Assert.False(Plan.Cal.IsWorkingDay(Plan.D(12, 8)));
        Assert.False(Plan.Cal.IsWorkingDay(Plan.D(10, 3)));  // sabato
        Assert.True(Plan.Cal.IsWorkingDay(Plan.D(10, 5)));
    }

    [Fact]
    public void GiorniLavorativi()
    {
        WorkCalendar cal = Plan.Cal;
        Assert.Equal(Plan.D(10, 12), cal.AddWorkingDays(Plan.D(10, 9), 1));
        Assert.Equal(Plan.D(12, 28), cal.AddWorkingDays(Plan.D(12, 24), 1));   // Natale venerdi', poi il fine settimana
        Assert.Equal(Plan.D(10, 9), cal.AddWorkingDays(Plan.D(10, 12), -1));
        Assert.Equal(5, cal.WorkingDays(Plan.D(10, 5), Plan.D(10, 11)));
        Assert.Equal(Plan.D(10, 12), cal.EndFor(Plan.D(10, 8), 3));
        Assert.Equal(3, cal.Duration(Plan.D(10, 8), Plan.D(10, 12)));
        Assert.Equal(Plan.D(10, 5), cal.NextWorkingDay(Plan.D(10, 3)));
    }

    [Fact]
    public void CommessaCheLavoraNelFineSettimana()
    {
        WorkCalendar cal = new(workOnWeekends: true, italianHolidays: false);
        Assert.Equal(Plan.D(10, 10), cal.AddWorkingDays(Plan.D(10, 9), 1));
        Assert.True(cal.IsWorkingDay(Plan.D(12, 25)));
    }
}

public class PlanSchedulerTests
{
    private static (List<PlanTask> Tasks, List<PlanLink> Links) Chain()
    {
        // A lun 5 - mer 7, B gio 8 - ven 9, C (milestone) lun 12: A -> B -> C
        List<PlanTask> tasks = new()
        {
            Plan.T(1, "A", Plan.D(10, 5), Plan.D(10, 7)),
            Plan.T(2, "B", Plan.D(10, 8), Plan.D(10, 9)),
            Plan.T(3, "C", Plan.D(10, 12), Plan.D(10, 12), PlanKind.Milestone),
        };
        return (tasks, new List<PlanLink> { new(10, 1, 2), new(11, 2, 3) });
    }

    [Fact]
    public void SlittamentoInCascataConservaLaDurata()
    {
        (List<PlanTask> tasks, List<PlanLink> links) = Chain();

        ScheduleResult r = PlanScheduler.Propagate(tasks, links, Plan.Edit(1, Plan.D(10, 5), Plan.D(10, 9)), Plan.Cal);

        Assert.Empty(r.Conflicts);
        Assert.True(r.MovesOthers);
        DateChange b = r.Changes.Single(c => c.TaskId == 2);
        Assert.True(b.Cascade);
        Assert.Equal((Plan.D(10, 12), Plan.D(10, 13)), (b.NewStart, b.NewEnd));
        DateChange c = r.Changes.Single(x => x.TaskId == 3);
        Assert.Equal((Plan.D(10, 14), Plan.D(10, 14)), (c.NewStart, c.NewEnd));
        Assert.False(r.Changes.Single(x => x.TaskId == 1).Cascade);
    }

    [Fact]
    public void RitardoInGiorniLavorativi()
    {
        (List<PlanTask> tasks, _) = Chain();
        List<PlanLink> links = new() { new(10, 1, 2, LagDays: 2) };

        ScheduleResult r = PlanScheduler.Propagate(tasks, links, Plan.Edit(1, Plan.D(10, 5), Plan.D(10, 9)), Plan.Cal);

        DateChange b = r.Changes.Single(c => c.TaskId == 2);
        Assert.Equal((Plan.D(10, 14), Plan.D(10, 15)), (b.NewStart, b.NewEnd));
    }

    [Fact]
    public void AnticipareNonAnticipaISuccessori()
    {
        (List<PlanTask> tasks, List<PlanLink> links) = Chain();

        ScheduleResult r = PlanScheduler.Propagate(tasks, links, Plan.Edit(1, Plan.D(10, 1), Plan.D(10, 2)), Plan.Cal);

        Assert.Equal(1, Assert.Single(r.Changes).TaskId);
        Assert.False(r.MovesOthers);
    }

    [Fact]
    public void FissatiEChiusiNonSiSpostano()
    {
        (List<PlanTask> tasks, List<PlanLink> links) = Chain();
        tasks[1].Locked = true;

        ScheduleResult r = PlanScheduler.Propagate(tasks, links, Plan.Edit(1, Plan.D(10, 5), Plan.D(10, 9)), Plan.Cal);

        PlanConflict conflict = Assert.Single(r.Conflicts);
        Assert.Equal(2, conflict.TaskId);
        Assert.StartsWith("data fissata", conflict.Reason);
        Assert.DoesNotContain(r.Changes, c => c.TaskId is 2 or 3);

        tasks[1].Locked = false;
        tasks[1].Status = PlanStatus.Done;
        Assert.StartsWith("fatta", PlanScheduler.Propagate(tasks, links, Plan.Edit(1, Plan.D(10, 5), Plan.D(10, 9)), Plan.Cal).Conflicts.Single().Reason);
    }

    [Fact]
    public void SpostatoAManoPrimaDelPredecessoreESegnalato()
    {
        (List<PlanTask> tasks, List<PlanLink> links) = Chain();

        ScheduleResult r = PlanScheduler.Propagate(tasks, links, Plan.Edit(2, Plan.D(10, 7), Plan.D(10, 8)), Plan.Cal);

        Assert.Contains("comincia prima della fine di \"A\"", Assert.Single(r.Conflicts).Reason);
        Assert.Single(PlanScheduler.ViolatedLinks(new[]
        {
            tasks[0], Plan.T(2, "B", Plan.D(10, 7), Plan.D(10, 8)), tasks[2],
        }, links, Plan.Cal));
    }

    [Fact]
    public void CicliRiconosciuti()
    {
        (List<PlanTask> tasks, List<PlanLink> links) = Chain();

        Assert.True(PlanScheduler.WouldCreateCycle(links, 3, 1));
        Assert.True(PlanScheduler.WouldCreateCycle(links, 2, 2));
        Assert.False(PlanScheduler.WouldCreateCycle(links, 1, 3));
        links.Add(new PlanLink(12, 3, 1));
        Assert.True(PlanScheduler.Propagate(tasks, links, Plan.Edit(1, Plan.D(10, 5), Plan.D(10, 9)), Plan.Cal).HasCycle);
    }

    [Fact]
    public void FasiDaiFigliEKpi()
    {
        PlanTask phase = Plan.T(1, "Software", Plan.D(9, 1), Plan.D(9, 1), PlanKind.Phase);
        PlanTask a = Plan.T(2, "Blocchi", Plan.D(10, 5), Plan.D(10, 7));
        PlanTask b = Plan.T(3, "HMI", Plan.D(10, 8), Plan.D(10, 9));
        PlanTask m = Plan.T(4, "FAT", Plan.D(10, 16), Plan.D(10, 16), PlanKind.Milestone);
        a.ParentId = b.ParentId = phase.Id;
        a.Status = PlanStatus.Done;
        List<PlanTask> all = new() { phase, a, b, m };

        PlanRollup.Apply(all, Plan.Cal);

        Assert.Equal((Plan.D(10, 5), Plan.D(10, 9)), (phase.Start, phase.End));
        Assert.Equal(60, phase.Progress); // 3 giorni fatti su 5
        Assert.Equal(PlanStatus.InProgress, phase.Status);

        PlanKpi k = PlanKpi.Compute(all, Plan.D(10, 12), Plan.Cal);
        Assert.Equal(3, k.Tasks);
        Assert.Equal(1, k.Done);
        Assert.Equal(1, k.Late);        // HMI doveva finire venerdi' 9
        Assert.Equal("FAT", k.NextMilestone?.Title);
        Assert.Equal(1, k.DueThisWeek); // FAT entro domenica 18
        Assert.Equal(Plan.D(10, 16), k.PlanEnd);
    }
}

public class PlanDocumentTests
{
    [Fact]
    public void PianoConFasiDipendenzeEVariazioni()
    {
        PlanTask phase = Plan.T(1, "Software", Plan.D(10, 5), Plan.D(10, 5), PlanKind.Phase);
        PlanTask a = Plan.T(2, "Blocchi", Plan.D(10, 5), Plan.D(10, 14));
        PlanTask b = Plan.T(3, "HMI", Plan.D(10, 15), Plan.D(10, 19));
        a.ParentId = b.ParentId = phase.Id;
        a.Sort = 10;
        b.Sort = 20;
        a.VersionId = 7;
        List<PlanEvent> events = new()
        {
            new() { Id = 1, Kind = PlanEventKinds.Dates, TaskTitle = "Blocchi", Field = PlanFields.Dates, Reason = "quadri in ritardo",
                OldValue = PlanFields.Range(Plan.D(10, 5), Plan.D(10, 9)), NewValue = PlanFields.Range(Plan.D(10, 5), Plan.D(10, 14)) },
            new() { Id = 2, Kind = PlanEventKinds.Dates, TaskTitle = "HMI", Field = PlanFields.Dates, Cascade = true,
                OldValue = PlanFields.Range(Plan.D(10, 12), Plan.D(10, 14)), NewValue = PlanFields.Range(Plan.D(10, 15), Plan.D(10, 19)) },
            new() { Id = 3, Kind = PlanEventKinds.Dates, TaskTitle = "HMI", Field = PlanFields.Dates, UndoneBy = "x",
                OldValue = PlanFields.Range(Plan.D(10, 15), Plan.D(10, 19)), NewValue = PlanFields.Range(Plan.D(10, 16), Plan.D(10, 20)) },
        };

        DocDocument d = DocumentBuilders.PlanList(DocumentBuilderTests.Ctx(), new[] { phase, a, b },
            new[] { new PlanLink(9, 2, 3) }, events, new Dictionary<long, string> { [7] = "V0.70" }, Plan.Cal);

        DocTable plan = d.Primary!.Table!;
        Assert.StartsWith("Fase: Software · 05/10/26 - 19/10/26", plan.Rows[0].Cells[0]!.Trim());
        Assert.Equal("Blocchi", plan.Rows[1].Cells[0]!.Trim());
        Assert.Equal("V0.70", plan.Rows[1].Cells[^1]);
        Assert.Equal("Blocchi", plan.Rows[2].Cells[9]);
        DocTable changes = d.Sections[1].Table!;
        Assert.Equal(2, changes.Rows.Count); // l'annullata non c'e'
        Assert.Equal("fine +5", changes.Rows[0].Cells[4]);
        Assert.Equal("+3", changes.Rows[1].Cells[4]);
        Assert.Equal("cascata", changes.Rows[1].Cells[6]);
        Assert.Equal("AUT123456_Piano_attivita", d.FileStem);
    }
}

public class PlanRepositoryTests
{
    private static (Db Db, PlanRepository Repo, long Commessa) Open()
    {
        Db db = Db.OpenInMemory();
        Commessa c = new() { Code = "AUT1", Name = "X" };
        new CommessaRepository(db).Insert(c);
        return (db, new PlanRepository(db), c.Id);
    }

    [Fact]
    public void CascataInUnLottoAnnullabile()
    {
        (Db db, PlanRepository repo, long c) = Open();
        using (db)
        {
            PlanTask a = new() { CommessaId = c, Title = "Software", Start = Plan.D(10, 5), End = Plan.D(10, 7) };
            PlanTask b = new() { CommessaId = c, Title = "Collaudo", Start = Plan.D(10, 8), End = Plan.D(10, 9) };
            repo.Insert(a, "utente");
            repo.Insert(b, "utente");
            repo.AddLink(a.Id, b.Id, 0, "utente");

            ScheduleResult r = PlanScheduler.Propagate(repo.Tasks(c), repo.Links(c), Plan.Edit(a.Id, Plan.D(10, 5), Plan.D(10, 9)), Plan.Cal);
            string batch = repo.ApplyDateChanges(c, r.Changes, "cliente in ritardo", "utente");

            Assert.Equal(Plan.D(10, 12), repo.Get(b.Id)!.Start);
            List<PlanEvent> events = repo.Events(c).Where(e => e.Batch == batch).ToList();
            Assert.Equal(2, events.Count);
            PlanEvent cascade = events.Single(e => e.TaskId == b.Id);
            Assert.True(cascade.Cascade);
            Assert.Equal(4, cascade.ShiftDays);
            Assert.All(events, e => Assert.Equal("cliente in ritardo", e.Reason));
            Assert.Equal(batch, repo.LastUndoableBatch(c));

            PlanUndoResult undo = repo.UndoBatch(batch, "utente");
            Assert.True(undo.Ok, undo.Message);
            Assert.Equal((Plan.D(10, 8), Plan.D(10, 9)), (repo.Get(b.Id)!.Start, repo.Get(b.Id)!.End));
            Assert.False(repo.UndoBatch(batch, "utente").Ok);
        }
    }

    [Fact]
    public void AnnullamentoRifiutatoSeIlTaskECambiatoDopo()
    {
        (Db db, PlanRepository repo, long c) = Open();
        using (db)
        {
            PlanTask a = new() { CommessaId = c, Title = "Software", Start = Plan.D(10, 5), End = Plan.D(10, 7) };
            repo.Insert(a, null);
            string batch = repo.ApplyDateChanges(c,
                new[] { new DateChange(a.Id, a.Title, a.Start, a.End, Plan.D(10, 6), Plan.D(10, 8), false) }, null, null);
            PlanTask again = repo.Get(a.Id)!;
            again.Start = Plan.D(10, 12);
            again.End = Plan.D(10, 14);
            repo.Update(again, null);

            PlanUndoResult undo = repo.UndoBatch(batch, null);

            Assert.False(undo.Ok);
            Assert.Contains("cambiato di nuovo", undo.Message);
            Assert.Equal(Plan.D(10, 12), repo.Get(a.Id)!.Start);
        }
    }

    [Fact]
    public void CampiNelloStoricoETitoloDopoLEliminazione()
    {
        (Db db, PlanRepository repo, long c) = Open();
        using (db)
        {
            PlanTask a = new() { CommessaId = c, Title = "Montaggio", Start = Plan.D(10, 5), End = Plan.D(10, 7) };
            repo.Insert(a, "utente");
            PlanTask edited = repo.Get(a.Id)!;
            edited.Status = PlanStatus.InProgress;
            edited.Assignee = "Mario";
            edited.Progress = 30;
            string? batch = repo.Update(edited, "utente", "partiti in anticipo");

            List<PlanEvent> fields = repo.Events(c, a.Id).Where(e => e.Batch == batch).ToList();
            Assert.Equal(new[] { "assignee", "progress", "status" }, fields.Select(e => e.Field).Order());
            Assert.Null(repo.Update(repo.Get(a.Id)!, "utente")); // nulla da salvare
            Assert.True(repo.UndoBatch(batch!, "utente").Ok);
            Assert.Equal(PlanStatus.ToDo, repo.Get(a.Id)!.Status);
            Assert.Null(repo.Get(a.Id)!.Assignee);

            repo.Delete(a.Id, "utente");
            Assert.Null(repo.Get(a.Id));
            Assert.Contains(repo.Events(c), e => e.Kind == PlanEventKinds.Deleted && e.TaskTitle == "Montaggio");
        }
    }

    [Fact]
    public void FasiEOrdine()
    {
        (Db db, PlanRepository repo, long c) = Open();
        using (db)
        {
            PlanTask phase = new() { CommessaId = c, Title = "Software", Kind = PlanKind.Phase, Start = Plan.D(10, 5), End = Plan.D(10, 5) };
            PlanTask a = new() { CommessaId = c, Title = "Blocchi", Start = Plan.D(10, 5), End = Plan.D(10, 7) };
            repo.Insert(phase, null);
            repo.Insert(a, null);
            repo.Reorder(c, new[] { (phase.Id, (long?)null, 10), (a.Id, (long?)phase.Id, 20) }, null);

            Assert.Equal(phase.Id, repo.Get(a.Id)!.ParentId);
            Assert.Contains(repo.Events(c, a.Id), e => e.Field == "parent_id");

            repo.Delete(phase.Id, null);
            Assert.Null(repo.Get(a.Id)!.ParentId); // i figli restano, al livello sopra
        }
    }

    [Fact]
    public void MigrazionePianificazioneDaV5()
    {
        using Db db = Db.OpenInMemory(5);
        db.Migrate();
        Assert.Equal(Db.LatestSchemaVersion, db.SchemaVersion);
        Assert.NotNull(db.Scalar("SELECT name FROM sqlite_master WHERE name = 'task_event'"));
    }
}
