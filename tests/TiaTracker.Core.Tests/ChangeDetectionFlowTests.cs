using TiaTracker.Core.Compare;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Reconcile;
using TiaTracker.Core.Scanning;
using TiaTracker.Data;

namespace TiaTracker.Core.Tests;

/// <summary>
/// Il giro di ChangeDetection (App) sul DB vero: snapshot salvati, base scelta, confronto
/// registrato, bozza salvata fra le modifiche e riletta al giro dopo.
/// </summary>
public class ChangeDetectionFlowTests
{
    private sealed class World : IDisposable
    {
        public readonly FakeTree Tree = new();
        public readonly Db Db = Db.OpenInMemory();
        public readonly Commessa C = new() { Code = "AUT123456", Name = "DEMO" };
        public readonly Dictionary<string, ProjectVersion> V;

        public World()
        {
            foreach (string label in new[] { "V0.68", "V0.69", "V0.70" })
            {
                Tree.File($@"AUT123456_Demo_{label}-V21\AUT123456_Demo_{label}-V21.ap21", "x");
            }

            new CommessaRepository(Db).Insert(C);
            ScanRoot root = new() { CommessaId = C.Id, Path = Tree.Root };
            new CommessaRepository(Db).InsertScanRoot(root);
            new VersionRepository(Db).ApplyScan(C.Id, root, ProjectFolderScanner.Scan(Tree.Root));
            V = new VersionRepository(Db).ByCommessa(C.Id).ToDictionary(v => v.Label);
        }

        public ChangeRepository Changes => new(Db);

        public long Snap(string label, int minute, params SnapshotItem[] items)
        {
            Snapshot s = new()
            {
                VersionId = V[label].Id, PlcName = "PLC_1", Source = SnapshotSources.File, ItemCount = items.Length,
                CreatedUtc = new DateTime(2026, 10, 5, 8, minute, 0, DateTimeKind.Utc),
            };
            return new SnapshotRepository(Db).Insert(s, items);
        }

        /// <summary>Come ChangeDetection.Detect, senza AppServices.</summary>
        public Change? Detect(string label, long snapshotId)
        {
            ProjectVersion version = V[label];
            SnapshotRepository snaps = new(Db);
            CompareRepository compares = new(Db);
            Dictionary<long, ProjectVersion> versions = new VersionRepository(Db).ByCommessa(C.Id).ToDictionary(v => v.Id);
            List<Snapshot> all = snaps.ByCommessa(C.Id);
            List<Change> changes = Changes.ByCommessa(C.Id);

            Snapshot target = all.Single(s => s.Id == snapshotId);
            Snapshot? baseSnap = SnapshotDraft.PickBase(target, all, versions);
            if (baseSnap == null)
            {
                return null;
            }

            List<CompareEntry> entries = SnapshotComparer.Compare(snaps.Items(baseSnap.Id), snaps.Items(target.Id));
            long runId = compares.SaveRun(baseSnap.Id, target.Id, entries, "automatico");
            ReconcileResult r = Reconciler.Reconcile(entries, changes, version.Id, baseSnap.VersionId, compares.Links(version.Id), target);
            DetectedPlc plc = new(target, baseSnap, versions[baseSnap.VersionId].Label, r.Rows, runId);
            Change? draft = SnapshotDraft.Build(C.Id, version, new[] { plc }, changes, new DateTime(2026, 10, 5, 10, 0, 0), versions);
            if (draft != null)
            {
                Changes.Save(draft, "rilevata dallo snapshot");
            }

            return draft;
        }

        public void Dispose()
        {
            Db.Dispose();
            Tree.Dispose();
        }
    }

    private static SnapshotItem I(string name, string all, string code = "c") => new()
    {
        Name = name, Kind = "FB", Family = "block", GroupPath = "G", State = "ok",
        HAll = all, HCode = code, HIface = "i", HInit = "v", HText = "t", HMeta = "m", Language = "SCL",
    };

    [Fact]
    public void OgniSnapshotSalvaLeModificheRilevate()
    {
        using World w = new();

        // Primo snapshot in assoluto: niente con cui confrontare.
        long s1 = w.Snap("V0.70", 0, I("A_FB", "a1"), I("B_FB", "b1"));
        Assert.Null(w.Detect("V0.70", s1));

        // Secondo: A cambiato, C nuovo -> bozza nelle modifiche della commessa.
        long s2 = w.Snap("V0.70", 10, I("A_FB", "a2", code: "c2"), I("B_FB", "b1"), I("C_FB", "c1"));
        Change first = w.Detect("V0.70", s2)!;

        Change saved = Assert.Single(w.Changes.ByCommessa(w.C.Id));
        Assert.Equal(first.Id, saved.Id);
        Assert.True(saved.Draft);
        Assert.Equal(new[] { "A_FB", "C_FB" }, saved.Blocks.Select(b => b.BlockName).OrderBy(n => n));
        ChangeVersion cv = Assert.Single(saved.Versions);
        Assert.Equal(w.V["V0.70"].Id, cv.VersionId);
        Assert.Equal(ChangeState.Saved, cv.State);
        Assert.NotNull(cv.EvidenceRunId);
        Assert.Contains(new CompareRepository(w.Db).RunsForVersion(w.V["V0.70"].Id), r => r.Id == cv.EvidenceRunId);

        // Terzo: A cambia ancora (gia' nella bozza), B cambia ora -> seconda bozza solo con B.
        long s3 = w.Snap("V0.70", 20, I("A_FB", "a3", code: "c3"), I("B_FB", "b2", code: "x"), I("C_FB", "c1"));
        Change second = w.Detect("V0.70", s3)!;
        Assert.Equal(new[] { "B_FB" }, second.Blocks.Select(b => b.BlockName));
        Assert.Equal(2, w.Changes.ByCommessa(w.C.Id).Count);

        // Quarto: niente di nuovo -> nessuna bozza.
        long s4 = w.Snap("V0.70", 30, I("A_FB", "a3", code: "c3"), I("B_FB", "b2", code: "x"), I("C_FB", "c1"));
        Assert.Null(w.Detect("V0.70", s4));
        Assert.Equal(2, w.Changes.ByCommessa(w.C.Id).Count);
    }

    [Fact]
    public void LeModificheDelleVersioniIntermedieSenzaSnapshotNonFinisconoNellaBozza()
    {
        using World w = new();
        w.Snap("V0.68", 0, I("A_FB", "a1"), I("B_FB", "b1"));

        // V0.69 non ha snapshot ma ha la sua modifica dichiarata su A.
        w.Changes.Save(new Change
        {
            CommessaId = w.C.Id, Title = "A su V0.69", Blocks = { new ChangeBlock { BlockName = "A_FB" } },
            Versions = { new ChangeVersion { VersionId = w.V["V0.69"].Id, State = ChangeState.Saved } },
        });

        // Primo snapshot di V0.70, confrontato con V0.68: A arriva da V0.69, B e' lavoro nuovo.
        long s = w.Snap("V0.70", 10, I("A_FB", "a2", code: "c2"), I("B_FB", "b2", code: "x"));
        Change draft = w.Detect("V0.70", s)!;

        Assert.Equal(new[] { "B_FB" }, draft.Blocks.Select(b => b.BlockName));
        Assert.Contains("V0.68", draft.Excerpt);
    }
}
