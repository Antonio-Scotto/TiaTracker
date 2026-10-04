using TiaTracker.Core.Domain;
using TiaTracker.Core.Scanning;
using TiaTracker.Data;

namespace TiaTracker.Core.Tests;

public class RepositoryTests
{
    private static (Db Db, Commessa C, ScanRoot Root, List<ProjectVersion> Versions) Seed(FakeTree t)
    {
        t.File(@"AUT123456_Demo_V0.68-V21\AUT123456_Demo_V0.68-V21.ap21", "x");
        t.File(@"AUT123456_Demo_V0.70-V21\AUT123456_Demo_V0.70-V21.ap21", "x");
        t.File(@"AUT123456_Demo_V0.57-V21.rar", "r");

        Db db = Db.OpenInMemory();
        CommessaRepository commesse = new(db);
        Commessa c = new() { Code = "AUT123456", Name = "DEMO" };
        commesse.Insert(c);
        ScanRoot root = new() { CommessaId = c.Id, Path = t.Root };
        commesse.InsertScanRoot(root);
        VersionRepository versions = new(db);
        versions.ApplyScan(c.Id, root, ProjectFolderScanner.Scan(t.Root));
        return (db, c, root, versions.ByCommessa(c.Id));
    }

    [Fact]
    public void MigrazioneEScansione()
    {
        using FakeTree t = new();
        (Db db, Commessa c, ScanRoot root, List<ProjectVersion> list) = Seed(t);
        using (db)
        {
            Assert.Equal(Db.LatestSchemaVersion, db.SchemaVersion);
            Assert.Equal(new[] { "V0.70", "V0.68", "V0.57" }, list.Select(v => v.Label));

            // Seconda scansione: nessuna nuova, nessuna sparita.
            ScanSummary again = new VersionRepository(db).ApplyScan(c.Id, root, ProjectFolderScanner.Scan(t.Root));
            Assert.Equal(0, again.New);
            Assert.Equal(0, again.Missing);

            // La cartella sparisce: la versione resta, marcata mancante.
            Directory.Delete(Path.Combine(t.Root, "AUT123456_Demo_V0.68-V21"), true);
            ScanSummary gone = new VersionRepository(db).ApplyScan(c.Id, root, ProjectFolderScanner.Scan(t.Root));
            Assert.Equal(1, gone.Missing);
            Assert.True(new VersionRepository(db).ByCommessa(c.Id).Single(v => v.Label == "V0.68").Missing);
        }
    }

    [Fact]
    public void UnaSolaCaricataPerPlc()
    {
        using FakeTree t = new();
        (Db db, Commessa c, _, List<ProjectVersion> list) = Seed(t);
        using (db)
        {
            VersionRepository repo = new(db);
            long v68 = list.Single(v => v.Label == "V0.68").Id;
            long v70 = list.Single(v => v.Label == "V0.70").Id;

            repo.SetLoaded(v68, DateTime.UtcNow.AddDays(-3), "plc1", null);
            repo.SetLoaded(v70, DateTime.UtcNow, "plc1", "messa in servizio");
            Assert.False(repo.Get(v68)!.IsLoaded);
            Assert.True(repo.Get(v70)!.IsLoaded);

            // Un altro PLC puo' avere la sua.
            repo.SetLoaded(v68, DateTime.UtcNow, "plc_linea2", null);
            Assert.True(repo.Get(v68)!.IsLoaded);
            Assert.True(repo.Get(v70)!.IsLoaded);
            Assert.Contains(repo.Events(v68), e => e.Kind == "scaricata");
        }
    }

    [Fact]
    public void ModificaSuDueVersioni()
    {
        using FakeTree t = new();
        (Db db, Commessa c, _, List<ProjectVersion> list) = Seed(t);
        using (db)
        {
            ChangeRepository repo = new(db);
            long v68 = list.Single(v => v.Label == "V0.68").Id;
            long v70 = list.Single(v => v.Label == "V0.70").Id;

            Change change = new()
            {
                CommessaId = c.Id,
                Title = "Correzione nastro ST020",
                Date = new DateOnly(2026, 9, 30),
                Blocks = { new ChangeBlock { BlockName = "ST020_ConveyorHandle_FB" }, new ChangeBlock { BlockName = "ST030_FilterBooking_DB" } },
                Versions =
                {
                    new ChangeVersion { VersionId = v68, State = ChangeState.Compiled, Errors = 0, Warnings = 5 },
                    new ChangeVersion { VersionId = v70, State = ChangeState.Planned },
                },
            };
            repo.Save(change);

            Change loaded = repo.Get(change.Id)!;
            Assert.Equal(2, loaded.Blocks.Count);
            Assert.Equal(ChangeState.Compiled, loaded.Versions.Single(v => v.VersionId == v68).State);
            Assert.Equal(5, loaded.Versions.Single(v => v.VersionId == v68).Warnings);

            repo.SetVersionState(change.Id, v70, ChangeState.Saved);
            repo.RemoveFromVersion(change.Id, v68);
            loaded = repo.Get(change.Id)!;
            Assert.Single(loaded.Versions);
            Assert.Equal(ChangeState.Saved, loaded.Versions[0].State);
            Assert.True(repo.Events(change.Id).Count >= 4);
        }
    }

    [Fact]
    public void ContentStoreDeduplica()
    {
        using Db db = Db.OpenInMemory();
        ContentStore store = new(db);
        string a = store.Put("<x>uguale</x>", "xml");
        string b = store.Put("<x>uguale</x>", "xml");
        Assert.Equal(a, b);
        Assert.Equal("<x>uguale</x>", store.GetText(a));
        Assert.Equal(1L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM content")));
    }
}
