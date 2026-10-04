using TiaTracker.Core.Domain;
using TiaTracker.Core.Output;
using TiaTracker.Core.Scanning;
using TiaTracker.Core.Status;
using TiaTracker.Data;

namespace TiaTracker.Core.Tests;

public class StatesMigrationTests
{
    private static long AddVersion(Db db, long commessa, string label, int loaded, string? plc, string? loadedUtc, int active, int archived)
    {
        return db.Insert(
            @"INSERT INTO version (commessa_id, label, sort_key, project_name, is_loaded, loaded_plc, loaded_utc, is_active, is_archived, first_seen_utc)
              VALUES ($c, $l, $s, $p, $il, $plc, $lu, $ia, $ar, '2026-01-01T00:00:00.000Z')",
            ("$c", commessa), ("$l", label), ("$s", "V" + label), ("$p", "P_" + label), ("$il", loaded), ("$plc", plc),
            ("$lu", loadedUtc), ("$ia", active), ("$ar", archived));
    }

    [Fact]
    public void DaiVecchiFlagAgliStati()
    {
        using Db db = Db.OpenInMemory(2);
        long c = db.Insert("INSERT INTO commessa (code, name, created_utc) VALUES ('AUT123456', 'DEMO', '2026-01-01T00:00:00.000Z')");
        long v68 = AddVersion(db, c, "V0.68", 1, null, "2026-10-04T12:35:00.000Z", 1, 0); // caso reale: caricata e attiva
        long v70 = AddVersion(db, c, "V0.70", 0, null, null, 1, 0);
        long v57 = AddVersion(db, c, "V0.57", 0, null, null, 0, 1);
        long a = AddVersion(db, c, "A", 1, "PLC_1", "2026-09-01T10:00:00.000Z", 0, 0);
        long b = AddVersion(db, c, "B", 1, "plc_1", "2026-09-20T10:00:00.000Z", 0, 0); // stesso PLC, maiuscole diverse

        db.Migrate();

        VersionRepository repo = new(db);
        Assert.Equal(VersionState.Caricata, repo.Get(v68)!.State);
        Assert.Equal(VersionState.InLavoro, repo.Get(v70)!.State);
        Assert.Equal(VersionState.Archiviata, repo.Get(v57)!.State);
        Assert.Equal(VersionState.Caricata, repo.Get(b)!.State);
        Assert.Equal(VersionState.Vecchia, repo.Get(a)!.State);

        // Flag storici allineati agli stati.
        Assert.False(repo.Get(v68)!.IsActive);
        Assert.True(repo.Get(v68)!.IsLoaded);
        Assert.False(repo.Get(a)!.IsLoaded);

        List<VersionLoad> loads = repo.CurrentLoads(c);
        Assert.Equal(2, loads.Count);
        Assert.Contains(loads, l => l.VersionId == v68 && l.Plc == "");
        Assert.Contains(loads, l => l.VersionId == b && l.Plc == "plc_1");
        Assert.Contains(repo.Events(v68), e => e.Kind == "stato" && e.Detail!.Contains("era anche Attiva"));
    }
}

public class VersionLoadTests
{
    private static (Db Db, VersionRepository Repo, long C, long[] V) Seed(int count)
    {
        Db db = Db.OpenInMemory();
        CommessaRepository commesse = new(db);
        Commessa c = new() { Code = "AUT000001", Name = "PROVA" };
        commesse.Insert(c);
        long[] ids = new long[count];
        for (int i = 0; i < count; i++)
        {
            ids[i] = db.Insert(
                @"INSERT INTO version (commessa_id, label, sort_key, project_name, first_seen_utc)
                  VALUES ($c, $l, $s, $p, '2026-01-01T00:00:00.000Z')",
                ("$c", c.Id), ("$l", "V0." + (60 + i)), ("$s", "V00000.000" + (60 + i)), ("$p", "P_" + i));
        }

        return (db, new VersionRepository(db), c.Id, ids);
    }

    [Fact]
    public void PiuPlcELaPrecedenteDiventaVecchia()
    {
        (Db db, VersionRepository repo, long c, long[] v) = Seed(3);
        using (db)
        {
            DateTime t = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
            repo.RecordLoad(v[0], new[] { "PLC_A", "PLC_B" }, t, "prima messa in servizio");
            Assert.Equal(VersionState.Caricata, repo.Get(v[0])!.State);

            List<VersionRepository.Replaced> r1 = repo.RecordLoad(v[1], new[] { "plc_a" }, t.AddDays(1), null);
            Assert.Single(r1);
            Assert.False(r1[0].BecameOld); // ancora su PLC_B
            Assert.Equal(VersionState.Caricata, repo.Get(v[0])!.State);
            Assert.True(repo.Get(v[0])!.IsLoaded);
            Assert.Equal("PLC_B", repo.Get(v[0])!.LoadedPlc);

            List<VersionRepository.Replaced> r2 = repo.RecordLoad(v[2], new[] { "PLC_B" }, t.AddDays(2), null);
            Assert.True(Assert.Single(r2).BecameOld);
            Assert.Equal(VersionState.Vecchia, repo.Get(v[0])!.State);
            Assert.False(repo.Get(v[0])!.IsLoaded);
            Assert.Contains(repo.Events(v[0]), e => e.Kind == "scaricata");

            List<VersionLoad> current = repo.CurrentLoads(c);
            Assert.Equal(2, current.Count);
            Assert.Equal(v[1], current.Single(l => l.Plc.Equals("PLC_A", StringComparison.OrdinalIgnoreCase)).VersionId);
            Assert.Equal(v[2], current.Single(l => l.Plc == "PLC_B").VersionId);
        }
    }

    [Fact]
    public void InLavoroSulPlcNonDiventaVecchia()
    {
        (Db db, VersionRepository repo, _, long[] v) = Seed(2);
        using (db)
        {
            repo.RecordLoad(v[0], new[] { "PLC_1" }, DateTime.UtcNow.AddDays(-2), null);
            repo.SetState(v[0], VersionState.InLavoro); // modifiche online sulla versione caricata
            Assert.True(repo.Get(v[0])!.IsLoaded);
            Assert.True(repo.Get(v[0])!.IsActive);

            List<VersionRepository.Replaced> r = repo.RecordLoad(v[1], new[] { "PLC_1" }, DateTime.UtcNow, null);
            Assert.False(Assert.Single(r).BecameOld);
            Assert.Equal(VersionState.InLavoro, repo.Get(v[0])!.State);
            Assert.False(repo.Get(v[0])!.IsLoaded);
        }
    }

    [Fact]
    public void CaricataSoloConRecordLoadEStatiEsplicitiResettabili()
    {
        (Db db, VersionRepository repo, _, long[] v) = Seed(1);
        using (db)
        {
            Assert.Throws<ArgumentException>(() => repo.SetState(v[0], VersionState.Caricata));
            repo.SetState(v[0], VersionState.InCollaudo, "FAT");
            Assert.Equal("FAT", repo.Get(v[0])!.StateNote);
            repo.SetState(v[0], VersionState.Scartata);
            Assert.Equal(VersionState.Scartata, repo.Get(v[0])!.State);
            Assert.Null(repo.Get(v[0])!.StateNote);
            repo.SetState(v[0], VersionState.None);
            Assert.Equal(VersionState.None, repo.Get(v[0])!.State);
            Assert.Contains(repo.Events(v[0]), e => e.Kind == "stato" && e.Detail!.Contains("In collaudo (FAT)"));
        }
    }

    [Fact]
    public void SequenzeCasualiNonRomponoGliIndici()
    {
        (Db db, VersionRepository repo, long c, long[] v) = Seed(4);
        using (db)
        {
            Random rnd = new(20261004);
            string[] plcs = { "PLC_1", "plc_1", "PLC_2", "" };
            VersionState[] states = { VersionState.None, VersionState.InLavoro, VersionState.DaCaricare, VersionState.Vecchia, VersionState.Archiviata };
            DateTime t = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < 400; i++)
            {
                long id = v[rnd.Next(v.Length)];
                t = t.AddMinutes(rnd.Next(1, 600));
                switch (rnd.Next(4))
                {
                    case 0:
                    case 1:
                        repo.RecordLoad(id, plcs.OrderBy(_ => rnd.Next()).Take(rnd.Next(1, 3)).ToList(), t, null);
                        break;
                    case 2:
                        repo.EndLoads(id, null, "prova");
                        break;
                    default:
                        repo.SetState(id, states[rnd.Next(states.Length)]);
                        break;
                }

                List<VersionLoad> current = repo.CurrentLoads(c);
                Assert.Equal(current.Count, current.Select(l => l.Plc.ToUpperInvariant()).Distinct().Count());
                foreach (long x in v)
                {
                    ProjectVersion pv = repo.Get(x)!;
                    Assert.Equal(current.Any(l => l.VersionId == x), pv.IsLoaded);
                    Assert.Equal(pv.State == VersionState.InLavoro, pv.IsActive);
                    Assert.Equal(pv.State == VersionState.Archiviata, pv.IsArchived);
                }
            }
        }
    }
}

public class VersionStatusTests
{
    private static ProjectVersion V(long id, string sort, VersionState state = VersionState.None, bool folder = true, DateTime? saved = null) => new()
    {
        Id = id, Label = "V" + id, SortKey = sort, State = state, HasFolder = folder, LastSavedUtc = saved,
    };

    [Fact]
    public void VecchiaInAutomaticoSoloSenzaStatoEFuoriDalPlc()
    {
        ProjectVersion old = V(1, "V00000.00068"), newer = V(2, "V00000.00070");
        List<ProjectVersion> all = new() { old, newer };

        VersionStatusInfo s = VersionStatusCalculator.Compute(old, all, Array.Empty<VersionLoad>(), OpenState.Closed, null);
        Assert.Equal(VersionState.Vecchia, s.Effective);
        Assert.True(s.AutoOld);
        Assert.True(s.IsRetired);

        VersionLoad onPlc = new() { VersionId = 1, Plc = "PLC_1", LoadedUtc = DateTime.UtcNow };
        s = VersionStatusCalculator.Compute(old, all, new[] { onPlc }, OpenState.Closed, null);
        Assert.Equal(VersionState.None, s.Effective);
        Assert.False(s.IsRetired);
        Assert.Contains(s.Badges, b => b.Text == "Sul PLC");

        old.State = VersionState.Caricata;
        s = VersionStatusCalculator.Compute(old, all, new[] { onPlc }, OpenState.Closed, null);
        Assert.Equal("Caricata", s.ChipText);
        Assert.DoesNotContain(s.Badges, b => b.Text == "Sul PLC");
        Assert.Contains("PLC_1", s.ChipToolTip);
    }

    [Fact]
    public void SnapshotDaAggiornare()
    {
        DateTime saved = new(2026, 10, 2, 10, 0, 0, DateTimeKind.Utc);
        ProjectVersion v = V(1, "V1", VersionState.InLavoro, saved: saved);
        Snapshot before = new() { CreatedUtc = saved.AddHours(-1) };
        Snapshot after = new() { CreatedUtc = saved.AddHours(1) };

        Assert.True(VersionStatusCalculator.Compute(v, new[] { v }, Array.Empty<VersionLoad>(), OpenState.Closed, null, before).SnapshotStale);
        Assert.False(VersionStatusCalculator.Compute(v, new[] { v }, Array.Empty<VersionLoad>(), OpenState.Closed, null, after).SnapshotStale);
        Assert.True(VersionStatusCalculator.Compute(v, new[] { v }, Array.Empty<VersionLoad>(), OpenState.Closed, null, null).SnapshotStale);

        v.State = VersionState.Vecchia;
        Assert.False(VersionStatusCalculator.Compute(v, new[] { v }, Array.Empty<VersionLoad>(), OpenState.Closed, null, before).SnapshotStale);
    }

    [Fact]
    public void VersioneDiRiferimento()
    {
        ProjectVersion a = V(1, "V1"), b = V(2, "V2"), c = V(3, "V3", VersionState.Scartata);
        List<ProjectVersion> all = new() { a, b, c };
        Assert.Equal(2, ReferenceVersion.Pick(all, Array.Empty<VersionLoad>())!.Id);

        VersionLoad onA = new() { VersionId = 1, Plc = "PLC_1", LoadedUtc = DateTime.UtcNow };
        Assert.Equal(1, ReferenceVersion.Pick(all, new[] { onA })!.Id);

        a.State = VersionState.InLavoro;
        b.State = VersionState.InLavoro;
        Assert.Equal(2, ReferenceVersion.Pick(all, new[] { onA })!.Id);
        Assert.Equal(1, ReferenceVersion.Pick(all, new[] { onA }, forcedId: 1)!.Id);
    }

    [Fact]
    public void RegistroEJsonRiportanoStatoECarichi()
    {
        Commessa c = new() { Code = "AUT1", Name = "X" };
        ProjectVersion v = V(1, "V1", VersionState.InLavoro);
        VersionLoad l = new() { VersionId = 1, Plc = "PLC_1", LoadedUtc = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc) };

        string md = Reports.ChangeRegisterMarkdown(c, new[] { v }, Array.Empty<Change>(), new[] { l });
        Assert.Contains("in lavoro", md);
        Assert.Contains("sul PLC PLC_1 dal", md);

        string json = Reports.CommessaJson(c, new[] { v }, Array.Empty<Change>(), Array.Empty<IpDevice>(), new[] { l });
        Assert.Contains("\"stato\": \"in_lavoro\"", json);
        Assert.Contains("\"plc\": \"PLC_1\"", json);
    }
}
