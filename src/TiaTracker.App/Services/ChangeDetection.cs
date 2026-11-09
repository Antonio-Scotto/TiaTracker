using TiaTracker.Core.Compare;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Reconcile;
using TiaTracker.Core.Snapshots;

namespace TiaTracker.App.Services;

/// <summary>
/// Dopo ogni snapshot: confronto con lo snapshot precedente (salvato come quelli a mano),
/// riconciliazione con le modifiche della versione e, per i blocchi cambiati che nessuno
/// dichiara, una bozza da convalidare (SnapshotDraft). Letture e scritture del DB sul thread
/// UI, il confronto vero (canonicalizzazione per i rinominati) in background.
/// </summary>
public static class ChangeDetection
{
    /// <param name="Draft">La bozza creata, o null.</param>
    /// <param name="Summary">Riepilogo per il messaggio dello snapshot (vuoto se non c'era niente da confrontare).</param>
    public sealed record Result(Change? Draft, string Summary);

    public static async Task<Result> AfterSnapshot(ProjectVersion version, IReadOnlyList<long> snapshotIds)
    {
        try
        {
            return await Detect(version, snapshotIds);
        }
        catch (Exception ex)
        {
            // Lo snapshot e' gia' registrato: il confronto automatico non lo deve far fallire.
            Log.Error("Confronto automatico dopo lo snapshot di " + version.Label, ex);
            return new Result(null, "confronto automatico non riuscito (vedere il log)");
        }
    }

    private static async Task<Result> Detect(ProjectVersion version, IReadOnlyList<long> snapshotIds)
    {
        Dictionary<long, ProjectVersion> versions = AppServices.Versions.ByCommessa(version.CommessaId).ToDictionary(v => v.Id);
        List<Snapshot> all = AppServices.Snapshots.ByCommessa(version.CommessaId);
        List<Change> changes = AppServices.Changes.ByCommessa(version.CommessaId);
        List<ReconcileLinkInfo> links = AppServices.Compares.Links(version.Id);

        List<DetectedPlc> plcs = new();
        int changed = 0;
        foreach (long id in snapshotIds)
        {
            Snapshot? target = all.FirstOrDefault(s => s.Id == id);
            Snapshot? baseSnap = target != null && SnapshotDraft.IsComplete(target) ? SnapshotDraft.PickBase(target, all, versions) : null;
            if (target == null || baseSnap == null)
            {
                continue;
            }

            List<CompareEntry> entries = await CompareAsync(AppServices.Snapshots.Items(baseSnap.Id), AppServices.Snapshots.Items(target.Id));
            int n = entries.Count(e => e.IsChange);
            changed += n;
            string baseLabel = baseSnap.VersionId == version.Id ? "lo snapshot precedente di " + version.Label
                : versions.TryGetValue(baseSnap.VersionId, out ProjectVersion? bv) ? bv.Label : "?";
            long runId = AppServices.Compares.SaveRun(baseSnap.Id, target.Id, entries,
                "Confronto automatico dopo lo snapshot (" + target.PlcName + "): " + n + " blocchi cambiati rispetto a " + baseLabel);
            ReconcileResult r = Reconciler.Reconcile(entries, changes, version.Id, baseSnap.VersionId, links, target);
            plcs.Add(new DetectedPlc(target, baseSnap, baseLabel, r.Rows, runId));
        }

        if (plcs.Count == 0)
        {
            return new Result(null, "");
        }

        Change? draft = SnapshotDraft.Build(version.CommessaId, version, plcs, changes, DateTime.Now, versions);
        if (draft == null)
        {
            return new Result(null, changed == 0
                ? "nessun blocco cambiato rispetto a " + plcs[0].BaseLabel
                : changed + " blocchi cambiati, gia' dichiarati nelle modifiche");
        }

        if (draft.Blocks.Count > SnapshotDraft.MaxBlocks)
        {
            Log.Info("Snapshot di " + version.Label + ": " + draft.Blocks.Count + " blocchi non dichiarati, bozza non creata");
            return new Result(null, draft.Blocks.Count + " blocchi cambiati non dichiarati rispetto a " + plcs[0].BaseLabel +
                                    ": troppi per una bozza (cambio di versione TIA?), da rivedere nel Confronto");
        }

        AppServices.Changes.Save(draft, "rilevata dallo snapshot");
        Log.Info("Bozza " + draft.Id + " da snapshot di " + version.Label + ": " + string.Join(", ", draft.Blocks.Select(b => b.BlockName)));
        return new Result(draft, draft.Blocks.Count + (draft.Blocks.Count == 1 ? " blocco cambiato non dichiarato" : " blocchi cambiati non dichiarati") +
                                 ": bozza da convalidare");
    }

    /// <summary>
    /// Avviso che resta finche' non si chiude: lo snapshot dura minuti, di solito l'utente
    /// non sta guardando quando arriva (gli Info spariscono dopo 8 s).
    /// </summary>
    public static void AnnounceDraft(ProjectVersion version, Change draft)
    {
        long changeId = draft.Id;
        Notify.Warning("Modifiche da convalidare · " + version.Label, draft.Title,
            new NoticeAction("Convalida", () => ViewModels.ChangeEditing.OpenDraft(changeId)),
            new NoticeAction("Tutte le bozze", () => ViewModels.ChangeEditing.ShowDrafts(version.CommessaId)));
    }

    /// <summary>Righe canoniche dell'XML di un elemento, per le differenze per rete.</summary>
    public static IReadOnlyList<string>? CanonicalLines(SnapshotItem item) => Canonical(AppServices.Content.GetText(item.XmlRef));

    private static IReadOnlyList<string>? Canonical(string? xml) =>
        xml == null ? null : XmlCanonicalizer.ToText(XmlCanonicalizer.Canonicalize(xml)).Split('\n');

    /// <summary>
    /// Confronto fra due snapshot senza fermare la finestra. Dal DB (thread UI) si leggono
    /// solo gli XML che la ricerca dei rinominati puo' chiedere: spariti e comparsi dello
    /// stesso tipo dall'altra parte. Canonicalizzazione e confronto girano in background.
    /// </summary>
    public static Task<List<CompareEntry>> CompareAsync(IReadOnlyList<SnapshotItem> baseItems, IReadOnlyList<SnapshotItem> targetItems)
    {
        HashSet<string> baseKeys = baseItems.Select(i => i.Key).ToHashSet();
        HashSet<string> targetKeys = targetItems.Select(i => i.Key).ToHashSet();
        List<SnapshotItem> removed = baseItems.Where(i => i.IsComparable && !targetKeys.Contains(i.Key)).ToList();
        List<SnapshotItem> added = targetItems.Where(i => i.IsComparable && !baseKeys.Contains(i.Key)).ToList();
        HashSet<(string, string)> removedKinds = removed.Select(i => (i.Family, i.Kind)).ToHashSet();
        HashSet<(string, string)> addedKinds = added.Select(i => (i.Family, i.Kind)).ToHashSet();

        Dictionary<SnapshotItem, string?> texts = new(ReferenceEqualityComparer.Instance);
        foreach (SnapshotItem i in removed.Where(i => addedKinds.Contains((i.Family, i.Kind)))
                     .Concat(added.Where(i => removedKinds.Contains((i.Family, i.Kind)))))
        {
            texts[i] = AppServices.Content.GetText(i.XmlRef);
        }

        return Task.Run(() => SnapshotComparer.Compare(baseItems, targetItems,
            item => texts.TryGetValue(item, out string? xml) ? Canonical(xml) : null));
    }
}
