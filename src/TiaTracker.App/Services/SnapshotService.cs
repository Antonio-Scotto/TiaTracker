using System.Globalization;
using System.Windows;
using TiaTracker.App.ViewModels;
using TiaTracker.Contracts;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Snapshots;

namespace TiaTracker.App.Services;

/// <summary>
/// Uno snapshot dall'inizio alla fine: scelta dell'istanza o copia del
/// progetto, worker, ingestione nel DB, pulizia. Gli errori diventano
/// messaggi per l'utente, mai retry automatici.
/// </summary>
public static class SnapshotService
{
    /// <param name="ExitCode">Codice del worker quando il problema e' suo (per proporre Diagnostica).</param>
    public sealed record Outcome(bool Ok, string Message, List<long> SnapshotIds, int? ExitCode = null)
    {
        /// <summary>Il problema dipende dall'ambiente (worker, DLL, permessi): Diagnostica aiuta.</summary>
        public bool NeedsDiagnostics => ExitCode is ExitCodes.WorkerNotFound or ExitCodes.OpennessNotFound or ExitCodes.AccessDenied;
    }

    private static Outcome Fail(string message, int? exitCode = null) => new(false, message, new List<long>(), exitCode);

    /// <summary>
    /// L'istanza di TIA che ha aperto proprio il .apNN di questa versione; se nessuna
    /// corrisponde si sceglie a mano (rischio principale: dati associati alla versione
    /// sbagliata). Failure se TIA non e' aperto o l'utente annulla.
    /// </summary>
    public static async Task<(InstanceInfo? Match, Outcome? Failure)> FindInstance(ProjectVersion version, string? projectFile,
        WorkerRunner runner, JobState job, CancellationToken ct)
    {
        int major = version.TiaMajor ?? 21;
        job.Text = "Ricerca delle istanze di TIA Portal...";
        WorkerOutcome inst = await runner.RunAsync(major, "instances", new Dictionary<string, object?>(), null, ct);
        if (!inst.Ok)
        {
            return (null, Fail("Elenco delle istanze di TIA non riuscito. " + inst.Explain(), inst.ExitCode));
        }

        List<InstanceInfo> instances = inst.Result<InstancesResult>()?.Instances ?? new List<InstanceInfo>();
        InstanceInfo? match = instances.FirstOrDefault(i => SamePath(i.ProjectPath, projectFile));
        if (match != null)
        {
            return (match, null);
        }

        List<InstanceInfo> withProject = instances.Where(i => i.ProjectPath != null).ToList();
        if (withProject.Count == 0)
        {
            return (null, Fail(instances.Count == 0
                ? "TIA Portal non e' aperto."
                : "TIA Portal e' aperto ma senza progetti: aprire " + version.ProjectName + " e riprovare."));
        }

        match = Views.InstancePickerWindow.Pick(version, projectFile, withProject);
        return match == null
            ? (null, Fail("Annullato: nessuna istanza di TIA scelta per " + version.Label + "."))
            : (match, null);
    }

    /// <summary>Da TIA aperto: attach al PID che ha aperto proprio il .apNN di questa versione.</summary>
    public static async Task<Outcome> FromOpenTia(ProjectVersion version, string? projectFile, JobState job, CancellationToken ct)
    {
        int major = version.TiaMajor ?? 21;
        WorkerRunner runner = new();
        (InstanceInfo? match, Outcome? failure) = await FindInstance(version, projectFile, runner, job, ct);
        if (match == null)
        {
            return failure!;
        }

        string work = ProjectCopier.NewWorkDir();
        try
        {
            Dictionary<string, object?> p = new() { ["pid"] = match.Pid.ToString(CultureInfo.InvariantCulture), ["out"] = work };
            WorkerOutcome snap = await runner.RunAsync(major, "snapshot", p, m => Report(job, m), ct);
            return await Finish(version, snap, work, null, "PID " + match.Pid, job);
        }
        finally
        {
            ProjectCopier.Cleanup(work);
        }
    }

    /// <summary>Da file: copia della cartella progetto, apertura senza interfaccia.</summary>
    public static async Task<Outcome> FromFile(ProjectVersion version, string folder, JobState job, CancellationToken ct)
    {
        string work = ProjectCopier.NewWorkDir();
        try
        {
            job.Text = "Copia del progetto in una cartella di lavoro...";
            string project = await Task.Run(() => ProjectCopier.CopyProject(folder, Path.GetFileName(version.ProjectFileRel!), work), ct);
            Dictionary<string, object?> p = new() { ["project"] = project, ["out"] = Path.Combine(work, "out") };
            WorkerOutcome snap = await new WorkerRunner().RunAsync(version.TiaMajor ?? 21, "snapshot", p, m => Report(job, m), ct);
            return await Finish(version, snap, work, null, Path.Combine(folder, Path.GetFileName(version.ProjectFileRel!)), job);
        }
        catch (IOException ex)
        {
            return Fail("Copia del progetto fallita: " + ex.Message);
        }
        finally
        {
            ProjectCopier.Cleanup(work);
        }
    }

    /// <summary>Da un archivio (.zip di backup TIA o .rar): estrazione e apertura senza interfaccia.</summary>
    public static async Task<Outcome> FromArchive(ProjectVersion version, string archive, string source, JobState job, CancellationToken ct)
    {
        string work = ProjectCopier.NewWorkDir();
        try
        {
            job.Text = "Estrazione di " + Path.GetFileName(archive) + "...";
            string project = await Task.Run(() => ProjectCopier.ExtractArchive(archive, work), ct);
            Dictionary<string, object?> p = new() { ["project"] = project, ["out"] = Path.Combine(work, "out") };
            WorkerOutcome snap = await new WorkerRunner().RunAsync(version.TiaMajor ?? 21, "snapshot", p, m => Report(job, m), ct);
            return await Finish(version, snap, work, source, archive, job);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or SharpCompress.Common.ArchiveException)
        {
            return Fail("Estrazione di " + Path.GetFileName(archive) + " fallita: " + ex.Message);
        }
        finally
        {
            ProjectCopier.Cleanup(work);
        }
    }

    /// <summary>Da una cartella di export XML gia' fatta (toolkit o TIA): niente worker, niente TIA.</summary>
    public static async Task<Outcome> FromExportFolder(ProjectVersion version, string folder, string? plcName, JobState job)
    {
        job.Text = "Lettura della cartella di export...";
        job.Indeterminate = true;
        string work = ProjectCopier.NewWorkDir();
        try
        {
            string manifest = await Task.Run(() => ExportFolderManifest.Write(ExportFolderManifest.Build(folder, plcName), work));
            WorkerOutcome fake = new()
            {
                ExitCode = ExitCodes.Ok,
                Final = new WorkerMessage(WorkerLine.TypeResult, null, null, null, null, null, null, null, 0,
                    System.Text.Json.JsonSerializer.SerializeToElement(new SnapshotResult { Manifests = { manifest } }, WorkerRunner.JsonOptions)),
            };
            return await Finish(version, fake, work, SnapshotSources.Export, folder, job);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("Cartella illeggibile: " + ex.Message);
        }
        finally
        {
            ProjectCopier.Cleanup(work);
        }
    }

    private static async Task<Outcome> Finish(ProjectVersion version, WorkerOutcome snap, string work, string? sourceOverride, string detail, JobState job)
    {
        SnapshotResult? result = snap.Result<SnapshotResult>();
        List<string> manifests = result?.Manifests ?? new List<string>();

        // Annullato o TIA caduto: il worker lascia un manifest parziale, lo si tiene solo se l'utente vuole.
        if (!snap.Ok)
        {
            string msg = snap.Explain();
            List<string> partial = Directory.Exists(work)
                ? Directory.EnumerateFiles(work, "manifest.json", SearchOption.AllDirectories).ToList()
                : new List<string>();
            if (partial.Count == 0 || snap.ExitCode is not (ExitCodes.TiaCrashed or ExitCodes.Partial))
            {
                return Fail(msg, snap.ExitCode);
            }

            if (MessageBox.Show(msg + "\n\nRegistrare comunque lo snapshot parziale?", "Snapshot parziale",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return Fail(msg);
            }

            manifests = partial;
        }

        job.Text = "Normalizzazione e hash...";
        job.Indeterminate = true;
        DateTime? saved = version.LastSavedUtc;
        List<(IngestResult Result, MemoryContentStore Store)> ingested = await Task.Run(() =>
            manifests.Select(m =>
            {
                MemoryContentStore mem = new();
                return (SnapshotIngestor.Ingest(m, version.Id, mem, saved, sourceOverride, detail), mem);
            }).ToList());

        List<long> ids = new();
        using (Data.Db.Tx tx = AppServices.Db.Begin())
        {
            foreach ((IngestResult r, MemoryContentStore mem) in ingested)
            {
                mem.FlushTo(AppServices.Content);
                ids.Add(AppServices.Snapshots.Insert(r.Snapshot, r.Items));
            }

            tx.Commit();
        }

        // L'export grezzo resta in TiaTrackerOut\Snapshot (gli export da cartella ci sono gia').
        // La cartella si calcola qui (DB, thread UI); in background solo la copia.
        if (sourceOverride != SnapshotSources.Export)
        {
            job.Text = "Copia dell'export in TiaTrackerOut...";
            Core.Output.OutputLayout? layout = OutputService.Layout(version.CommessaId);
            for (int i = 0; i < ingested.Count && i < manifests.Count && layout != null; i++)
            {
                Snapshot s = ingested[i].Result.Snapshot;
                string dir = Path.GetDirectoryName(manifests[i])!;
                await Task.Run(() => OutputService.SaveSnapshotFiles(layout, version, s, dir));
            }
        }

        // Hardware, rete e tag dello stesso export (il worker li legge dopo i blocchi).
        string hardwareNote = "";
        if (result?.Hardware != null && File.Exists(result.Hardware) && ingested.Count > 0)
        {
            job.Text = "Lettura di hardware, rete e tag...";
            job.Indeterminate = true;
            HardwareService.Ingested? hw = await HardwareService.Ingest(version, result.Hardware, ingested[0].Result.Snapshot.Source, detail, ids.FirstOrDefault());
            if (hw != null)
            {
                string ip = HardwareService.AfterIngest(version, hw, force: false);
                string docs = await DocumentService.AfterHardware(version, job);
                hardwareNote = "; hardware: " + hw.Summary + (ip.Length > 0 ? "; " + ip : "") + (docs.Length > 0 ? "; " + docs : "");
            }
        }
        else if (snap.Warnings.FirstOrDefault(w => w.Code is "hardware-failed" or "hardware-skipped") is WorkerMessage hwWarn)
        {
            hardwareNote = "; hardware non letto: " + hwWarn.Text;
        }

        OutputService.Refresh(version.CommessaId);

        string summary = string.Join("; ", ingested.Select(x =>
        {
            Snapshot s = x.Result.Snapshot;
            int skipped = x.Result.Items.Count(i => i.State is ItemStates.Skipped or ItemStates.Protected);
            int changed = x.Result.Items.Count(i => i.ChangedDuringExport);
            return $"{s.PlcName}: {s.ItemCount} elementi, {s.FailedCount} falliti, {skipped} saltati" +
                   (changed > 0 ? $", {changed} cambiati durante l'export" : "") +
                   (s.ProjectModified == true ? " (progetto con modifiche NON salvate)" : "");
        }));
        summary += hardwareNote;
        Log.Info("Snapshot " + version.Label + ": " + summary);
        return new Outcome(true, summary, ids);
    }

    private static void Report(JobState job, WorkerMessage m)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            switch (m.Type)
            {
                case "status":
                    job.Text = m.Text ?? m.Code ?? "";
                    job.Indeterminate = true;
                    break;
                case "progress" when m.Total > 0:
                    job.Indeterminate = false;
                    job.Progress = (double)(m.Done ?? 0) / m.Total!.Value;
                    job.Text = PhaseText(m.Phase) + " " + m.Done + "/" + m.Total + "  " + m.Item;
                    break;
            }
        });
    }

    public static string PhaseText(string? phase) => phase switch
    {
        "attributes" => "Attributi",
        "export" => "Export",
        "verify" => "Verifica",
        "hardware" => "Hardware",
        "tags" => "Tabelle tag",
        _ => phase ?? "",
    };

    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
