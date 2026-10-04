using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using TiaTracker.App.ViewModels;
using TiaTracker.Contracts;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Hardware;
using TiaTracker.Core.Network;
using TiaTracker.Core.Output;
using TiaTracker.Core.Status;

namespace TiaTracker.App.Services;

/// <summary>La griglia IP salvi subito le modifiche in sospeso (prima di un allineamento).</summary>
public sealed record IpTableFlushRequest(long CommessaId);

/// <summary>La Lista IP della commessa e' cambiata fuori dalla griglia: ricaricarla.</summary>
public sealed record IpTableChanged(long CommessaId);

/// <summary>
/// Hardware, rete e tag da TIA: lettura (worker "hardware", oppure insieme allo
/// snapshot), salvataggio nel content store, allineamento della Lista IP. Il
/// parsing gira in background, il DB sul thread UI.
/// </summary>
public static class HardwareService
{
    /// <summary>Un export registrato: la riga e i dati letti.</summary>
    public sealed record Ingested(HwSnapshot Snapshot, HardwareExport Data)
    {
        public string Summary => Snapshot.DeviceCount + " dispositivi, " + Snapshot.ModuleCount + " moduli, " + Snapshot.IpCount + " IP, " +
                                 Snapshot.IoTagCount + " segnali I/O" + (Snapshot.Partial ? " (con avvisi)" : "");
    }

    private static readonly Dictionary<string, HardwareExport> Cache = new(StringComparer.Ordinal);

    // ---------- lettura da TIA ----------

    /// <summary>Solo hardware, rete e tag dal TIA che ha aperto la versione (pochi secondi, niente blocchi).</summary>
    public static async Task<JobOutcome> FromOpenTia(ProjectVersion version, string? projectFile, JobState job, CancellationToken ct,
        bool forceIpSync = false)
    {
        WorkerRunner runner = new();
        (InstanceInfo? match, SnapshotService.Outcome? failure) = await SnapshotService.FindInstance(version, projectFile, runner, job, ct);
        if (match == null)
        {
            return ToJob(failure!);
        }

        string work = ProjectCopier.NewWorkDir();
        try
        {
            Dictionary<string, object?> p = new() { ["pid"] = match.Pid.ToString(CultureInfo.InvariantCulture), ["out"] = work };
            WorkerOutcome o = await runner.RunAsync(version.TiaMajor ?? 21, "hardware", p, m => Report(job, m), ct);
            return await Finish(version, o, work, SnapshotSources.Attach, "PID " + match.Pid, job, forceIpSync);
        }
        finally
        {
            ProjectCopier.Cleanup(work);
        }
    }

    /// <summary>Da una copia della cartella aperta senza interfaccia (1-2 minuti).</summary>
    public static async Task<JobOutcome> FromFolder(ProjectVersion version, string folder, JobState job, CancellationToken ct,
        bool forceIpSync = false)
    {
        string work = ProjectCopier.NewWorkDir();
        try
        {
            job.Text = "Copia del progetto in una cartella di lavoro...";
            string project = await Task.Run(() => ProjectCopier.CopyProject(folder, Path.GetFileName(version.ProjectFileRel!), work), ct);
            Dictionary<string, object?> p = new() { ["project"] = project, ["out"] = Path.Combine(work, "out") };
            WorkerOutcome o = await new WorkerRunner().RunAsync(version.TiaMajor ?? 21, "hardware", p, m => Report(job, m), ct);
            return await Finish(version, o, work, SnapshotSources.File, Path.Combine(folder, Path.GetFileName(version.ProjectFileRel!)), job, forceIpSync);
        }
        catch (IOException ex)
        {
            return JobOutcome.Failed("Copia del progetto fallita: " + ex.Message);
        }
        finally
        {
            ProjectCopier.Cleanup(work);
        }
    }

    private static async Task<JobOutcome> Finish(ProjectVersion version, WorkerOutcome o, string work, string source, string detail, JobState job,
        bool forceIpSync)
    {
        HardwareResult? r = o.Ok ? o.Result<HardwareResult>() : null;
        if (r?.Hardware == null || !File.Exists(r.Hardware))
        {
            string msg = o.Ok ? "Il worker non ha scritto hardware.json." : o.Explain();
            return o.ExitCode is ExitCodes.WorkerNotFound or ExitCodes.OpennessNotFound or ExitCodes.AccessDenied
                ? JobOutcome.Failed(msg, JobRunner.DiagnosticsAction())
                : JobOutcome.Failed(msg);
        }

        job.Text = "Lettura di hardware, rete e tag...";
        Ingested? hw = await Ingest(version, r.Hardware, source, detail, null);
        if (hw == null)
        {
            return JobOutcome.Failed("Export hardware non registrato: vedere il log.");
        }

        string ip = AfterIngest(version, hw, force: forceIpSync);
        string docs = await DocumentService.AfterHardware(version, job);
        return JobOutcome.Done(version.Label + ": " + hw.Summary + (ip.Length > 0 ? ". " + ip : "") + (docs.Length > 0 ? ". Documenti: " + docs : ""),
            ListsAction(version));
    }

    // ---------- registrazione ----------

    /// <summary>
    /// Legge hardware.json (tag degli XML compresi) e lo registra: content store, riga
    /// hw_snapshot, copia in TiaTrackerOut\Snapshot. Chiamare prima di cancellare la
    /// cartella di lavoro. Null se il file non si legge.
    /// </summary>
    public static async Task<Ingested?> Ingest(ProjectVersion version, string hardwareJson, string source, string? detail, long? snapshotId)
    {
        HardwareExport data;
        string json;
        List<HwModuleRow> modules;
        int ipCount, ioCount, tagCount;
        try
        {
            (data, json, modules, ipCount, ioCount, tagCount) = await Task.Run(() =>
            {
                HardwareExport d = HardwareReader.Load(hardwareJson);
                List<HwModuleRow> m = HardwareView.Modules(d);
                int ips = IpSync.FromHardware(d).Count(x => x.Ip != null);
                int io = IoResolver.Resolve(d, m).Count;
                int tags = d.TagTables.Sum(t => t.Tags.Count);
                return (d, HardwareReader.Serialize(d), m, ips, io, tags);
            });
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error("hardware.json illeggibile: " + hardwareJson, ex);
            return null;
        }

        HwSnapshot s = new()
        {
            VersionId = version.Id,
            SnapshotId = snapshotId,
            CreatedUtc = DateTime.UtcNow,
            Source = source,
            SourceDetail = detail,
            ProjectPath = data.ProjectPath,
            ProjectModified = data.IsModified,
            WorkerVersion = data.WorkerVersion,
            TiaMajor = data.TiaMajor,
            Format = data.Format,
            DeviceCount = data.Devices.Count,
            ModuleCount = modules.Count,
            IpCount = ipCount,
            TagCount = tagCount,
            IoTagCount = ioCount,
            Partial = data.Partial,
            WarningsJson = data.Warnings.Count > 0 ? JsonSerializer.Serialize(data.Warnings) : null,
            TimingsJson = data.TimingsMs.Count > 0 ? JsonSerializer.Serialize(data.TimingsMs) : null,
        };

        using (Data.Db.Tx tx = AppServices.Db.Begin())
        {
            s.DataHash = AppServices.Content.Put(json, "hardware");
            AppServices.Hardware.Insert(s);
            tx.Commit();
        }

        lock (Cache)
        {
            Cache[s.DataHash] = data;
        }

        // L'export grezzo (json + XML dei tag) resta in TiaTrackerOut\Snapshot\<versione>\<data>_<sorgente>_hardware.
        OutputLayout? layout = OutputService.Layout(version.CommessaId);
        if (layout != null)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(hardwareJson))!;
            Snapshot fake = new() { CreatedUtc = s.CreatedUtc, Source = source, PlcName = "hardware" };
            await Task.Run(() => OutputService.SaveSnapshotFiles(layout, version, fake, dir));
        }

        Log.Info("Hardware " + version.Label + ": " + data.Devices.Count + " dispositivi, " + modules.Count + " moduli, " + ipCount + " IP, " +
                 tagCount + " tag (" + ioCount + " I/O), " + data.Warnings.Count + " avvisi");
        return new Ingested(s, data);
    }

    /// <summary>
    /// Dopo un export: Lista IP allineata se la versione e' quella di riferimento (o se
    /// <paramref name="force"/>), altrimenti una notifica che lo propone. Restituisce un riepilogo.
    /// </summary>
    public static string AfterIngest(ProjectVersion version, Ingested hw, bool force)
    {
        Commessa? c = AppServices.Commesse.Get(version.CommessaId);
        if (c == null)
        {
            return "";
        }

        CommessaSettings settings = CommessaSettings.Parse(c.SettingsJson);
        ProjectVersion? reference = Reference(c);
        bool isReference = reference?.Id == version.Id;
        if (force || (isReference && settings.AutoIpSync))
        {
            IpSyncSummary sync = SyncIp(version.CommessaId, version.Id, hw.Data);
            return "Lista IP: " + sync;
        }

        Notify.Info("Lista IP non toccata",
            version.Label + " non e' la versione di riferimento" + (reference != null ? " (" + reference.Label + ")" : "") + ".",
            new NoticeAction("Aggiorna IP da " + version.Label, () =>
            {
                IpSyncSummary s = SyncIp(version.CommessaId, version.Id, hw.Data);
                Notify.Success("Lista IP aggiornata da " + version.Label, s.ToString());
            }));
        return "";
    }

    /// <summary>Allinea la Lista IP ai nodi di rete dell'export e la salva.</summary>
    public static IpSyncSummary SyncIp(long commessaId, long versionId, HardwareExport data)
    {
        WeakReferenceMessenger.Default.Send(new IpTableFlushRequest(commessaId));
        List<IpDevice> existing = AppServices.Commesse.IpDevices(commessaId);
        (List<IpDevice> rows, IpSyncSummary summary) = IpSync.Merge(existing, IpSync.FromHardware(data), versionId, DateTime.UtcNow);
        if (summary.Changed)
        {
            AppServices.Commesse.SaveIpDevices(commessaId, rows);
            OutputService.Refresh(commessaId);
        }

        WeakReferenceMessenger.Default.Send(new IpTableChanged(commessaId));
        Log.Info("Lista IP commessa " + commessaId + ": " + summary);
        return summary;
    }

    /// <summary>La versione di riferimento della commessa (impostazione o automatica).</summary>
    public static ProjectVersion? Reference(Commessa c)
    {
        List<ProjectVersion> versions = AppServices.Versions.ByCommessa(c.Id);
        return ReferenceVersion.Pick(versions, AppServices.Versions.CurrentLoads(c.Id), CommessaSettings.Parse(c.SettingsJson).ReferenceVersionId);
    }

    /// <summary>I dati di un export (dal content store, con una piccola cache).</summary>
    public static HardwareExport? Load(HwSnapshot s)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(s.DataHash, out HardwareExport? cached))
            {
                return cached;
            }
        }

        string? json = AppServices.Content.GetText(s.DataHash);
        if (json == null)
        {
            return null;
        }

        HardwareExport data = HardwareReader.Deserialize(json);
        lock (Cache)
        {
            if (Cache.Count > 6)
            {
                Cache.Clear();
            }

            Cache[s.DataHash] = data;
        }

        return data;
    }

    /// <summary>Come <see cref="Load"/>, ma il JSON (alcuni MB su un impianto medio) si legge in background: il DB resta sul thread UI.</summary>
    public static async Task<HardwareExport?> LoadAsync(HwSnapshot s)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(s.DataHash, out HardwareExport? cached))
            {
                return cached;
            }
        }

        string? json = AppServices.Content.GetText(s.DataHash);
        if (json == null)
        {
            return null;
        }

        HardwareExport data = await Task.Run(() => HardwareReader.Deserialize(json));
        lock (Cache)
        {
            if (Cache.Count > 6)
            {
                Cache.Clear();
            }

            Cache[s.DataHash] = data;
        }

        return data;
    }

    /// <summary>[Apri Elenchi] dopo un export, se la commessa ha TiaTrackerOut.</summary>
    public static NoticeAction[] ListsAction(ProjectVersion version)
    {
        OutputLayout? layout = OutputService.Layout(version.CommessaId);
        return layout == null || !Directory.Exists(layout.Lists)
            ? Array.Empty<NoticeAction>()
            : new[] { new NoticeAction("Apri Elenchi", () => DocumentService.OpenFolder(layout.Lists)) };
    }

    private static JobOutcome ToJob(SnapshotService.Outcome r) =>
        r.NeedsDiagnostics ? JobOutcome.Failed(r.Message, JobRunner.DiagnosticsAction()) : JobOutcome.Failed(r.Message);

    private static void Report(JobState job, WorkerMessage m)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
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
                    job.Text = SnapshotService.PhaseText(m.Phase) + " " + m.Done + "/" + m.Total + "  " + m.Item;
                    break;
            }
        });
    }
}
