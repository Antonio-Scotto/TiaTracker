using System.Globalization;
using TiaTracker.App.ViewModels;
using TiaTracker.Contracts;
using TiaTracker.Core.Documents;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Network;
using TiaTracker.Core.Output;
using TiaTracker.Data;
using TiaTracker.Export;

namespace TiaTracker.App.Services;

/// <summary>I documenti di una commessa per una versione, con la fonte dei dati.</summary>
public sealed class DocBundle
{
    public required Commessa Commessa { get; init; }
    public ProjectVersion? Version { get; init; }

    /// <summary>La versione e' quella di riferimento: file in Elenchi\ (le altre in Elenchi\&lt;versione&gt;\).</summary>
    public bool IsReference { get; init; }
    public HwSnapshot? Hardware { get; init; }
    public Dictionary<string, DocDocument> Docs { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>Esito di una generazione: file scritti per documento.</summary>
public sealed record DocExportOutcome(string Folder, List<(string Kind, List<DocFileResult> Files)> Written)
{
    public IEnumerable<DocFileResult> All => Written.SelectMany(w => w.Files);

    public bool Ok => All.Any() && All.All(f => f.Ok);
}

/// <summary>
/// Lista IP, Lista hardware, Lista IO e registro: dati dal DB (thread UI),
/// costruzione e scrittura dei file in background, storico in doc_export.
/// </summary>
public static class DocumentService
{
    public static readonly IReadOnlyList<string> Kinds = new[] { DocKinds.Ip, DocKinds.Hardware, DocKinds.Io, DocKinds.Register, DocKinds.Plan };

    public static string Describe(string kind) => kind switch
    {
        DocKinds.Ip => "Indirizzi IP, maschere, nomi PROFINET e MAC per rete, dalla tabella Rete / IP",
        DocKinds.Hardware => "Stazioni e moduli con codici d'ordine, firmware e aree di indirizzi, letti da TIA",
        DocKinds.Io => "Segnali di ingresso e uscita delle tabelle dei tag, assegnati al modulo",
        DocKinds.Register => "Versioni con stato e carichi, modifiche con lo stato per versione",
        DocKinds.Plan => "Attivita', milestone e fasi con date e avanzamento, e le variazioni delle tempistiche",
        _ => "",
    };

    /// <summary>Raccoglie i dati e costruisce i documenti richiesti (tutti se <paramref name="kinds"/> e' null).</summary>
    public static async Task<DocBundle?> BuildAsync(long commessaId, long? versionId, IEnumerable<string>? kinds = null)
    {
        Commessa? c = AppServices.Commesse.Get(commessaId);
        if (c == null)
        {
            return null;
        }

        List<ProjectVersion> versions = AppServices.Versions.ByCommessa(commessaId);
        ProjectVersion? reference = HardwareService.Reference(c);
        ProjectVersion? version = versionId == null ? reference : versions.FirstOrDefault(v => v.Id == versionId) ?? reference;
        bool isReference = version == null || version.Id == reference?.Id;
        HwSnapshot? hw = version == null ? null : AppServices.Hardware.Latest(version.Id);
        HardwareExport? data = hw == null ? null : await HardwareService.LoadAsync(hw);

        DocContext ctx = new()
        {
            Commessa = c,
            Version = version,
            HardwareSource = data == null ? null : hw,
            Notes = AppServices.Docs.Notes(commessaId),
            ManualRows = AppServices.Docs.ManualRows(commessaId),
            GeneratedLocal = DateTime.Now,
        };
        HashSet<string> want = (kinds ?? Kinds).ToHashSet(StringComparer.Ordinal);
        List<IpDevice> ip = want.Contains(DocKinds.Ip) ? AppServices.Commesse.IpDevices(commessaId) : new List<IpDevice>();
        List<Change> changes = want.Contains(DocKinds.Register) ? AppServices.Changes.ByCommessa(commessaId, includeDrafts: false) : new List<Change>();
        List<VersionLoad> loads = AppServices.Versions.Loads(commessaId);

        // Piano: niente documento vuoto quando si generano tutti e la commessa non usa la pianificazione.
        List<Core.Planning.PlanTask> tasks = want.Contains(DocKinds.Plan) ? AppServices.Plans.Tasks(commessaId) : new List<Core.Planning.PlanTask>();
        if (tasks.Count == 0 && want.Count > 1)
        {
            want.Remove(DocKinds.Plan);
        }

        List<Core.Planning.PlanLink> planLinks = want.Contains(DocKinds.Plan) ? AppServices.Plans.Links(commessaId) : new List<Core.Planning.PlanLink>();
        List<Core.Planning.PlanEvent> planEvents = want.Contains(DocKinds.Plan) ? AppServices.Plans.Events(commessaId, null, 5000) : new List<Core.Planning.PlanEvent>();
        Dictionary<long, string> versionLabels = versions.ToDictionary(v => v.Id, v => v.Label);
        Core.Planning.WorkCalendar cal = new(CommessaSettings.Parse(c.SettingsJson).WorkOnWeekends);

        Dictionary<string, DocDocument> docs = await Task.Run(() =>
        {
            Dictionary<string, DocDocument> d = new(StringComparer.Ordinal);
            if (want.Contains(DocKinds.Ip))
            {
                // Per una versione che non e' quella di riferimento: i nodi di rete del suo export,
                // non la tabella della commessa (che segue la versione di riferimento).
                d[DocKinds.Ip] = !isReference && data != null
                    ? DocumentBuilders.IpList(ctx, IpSync.FromHardware(data), "Nodi di rete dell'export da TIA di " + version!.Label)
                    : DocumentBuilders.IpList(ctx, ip);
            }

            if (want.Contains(DocKinds.Hardware))
            {
                d[DocKinds.Hardware] = DocumentBuilders.HardwareList(ctx, data);
            }

            if (want.Contains(DocKinds.Io))
            {
                d[DocKinds.Io] = DocumentBuilders.IoList(ctx, data);
            }

            if (want.Contains(DocKinds.Register))
            {
                d[DocKinds.Register] = DocumentBuilders.ChangeRegister(ctx, versions, changes, loads);
            }

            if (want.Contains(DocKinds.Plan))
            {
                d[DocKinds.Plan] = DocumentBuilders.PlanList(ctx, tasks, planLinks, planEvents, versionLabels, cal);
            }

            return d;
        });

        return new DocBundle { Commessa = c, Version = version, IsReference = isReference, Hardware = data == null ? null : hw, Docs = docs };
    }

    /// <summary>
    /// Genera i file in TiaTrackerOut\Elenchi (o Elenchi\&lt;versione&gt;\) e li registra.
    /// Con <paramref name="notify"/> mostra l'esito con [Apri cartella]; null se manca TiaTrackerOut.
    /// </summary>
    public static async Task<DocExportOutcome?> ExportAsync(long commessaId, long? versionId, IReadOnlyCollection<string> kinds,
        IReadOnlyCollection<DocFormat> formats, bool notify = true)
    {
        OutputLayout? layout = OutputService.Layout(commessaId);
        if (layout == null)
        {
            if (notify)
            {
                Notify.Warning("Documenti non generati",
                    "La commessa non ha una cartella TiaTrackerOut: aggiungere una cartella di scansione o sceglierla nella Panoramica.");
            }

            return null;
        }

        DocBundle? b = await BuildAsync(commessaId, versionId, kinds);
        if (b == null)
        {
            return null;
        }

        string folder = layout.ListsDir(b.IsReference ? null : b.Version?.Label);
        List<(string Kind, List<DocFileResult> Files)> written;
        try
        {
            written = await Task.Run(() =>
            {
                layout.EnsureCreated();
                List<(string, List<DocFileResult>)> w = new();
                foreach (string kind in Kinds.Where(b.Docs.ContainsKey))
                {
                    // Registro e piano sono della commessa, non della versione: sempre in Elenchi\.
                    string dir = kind is DocKinds.Register or DocKinds.Plan ? layout.Lists : folder;
                    w.Add((kind, DocExporter.Write(b.Docs[kind], dir, formats)));
                }

                return w;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Documenti non scritti in " + folder + ": " + ex.Message);
            if (notify)
            {
                Notify.Error("Documenti non generati", ex.Message);
            }

            return new DocExportOutcome(folder, new List<(string, List<DocFileResult>)>());
        }

        DateTime now = DateTime.UtcNow;
        foreach ((string kind, List<DocFileResult> files) in written)
        {
            List<DocFileResult> ok = files.Where(f => f.Ok).ToList();
            if (ok.Count == 0)
            {
                continue;
            }

            AppServices.Docs.RecordExport(new DocExport
            {
                CommessaId = commessaId,
                ListKind = kind,
                VersionId = b.Version?.Id,
                HwSnapshotId = kind is DocKinds.Hardware or DocKinds.Io or DocKinds.Ip ? b.Hardware?.Id : null,
                GeneratedUtc = now,
                Formats = string.Join(",", ok.Select(f => DocExporter.Extension(f.Format).TrimStart('.'))),
                Files = ok.Select(f => f.Path).ToList(),
            });
        }

        DocExportOutcome outcome = new(folder, written);
        foreach (DocFileResult f in outcome.All.Where(f => !f.Ok))
        {
            Log.Warn("Documento non scritto: " + f.Path + ": " + f.Error);
        }

        Log.Info("Documenti " + b.Commessa.Code + " (" + (b.Version?.Label ?? "-") + "): " + outcome.All.Count(f => f.Ok) + " file in " + folder);
        if (notify)
        {
            Report(outcome, b);
        }

        return outcome;
    }

    /// <summary>
    /// Dopo un export hardware: se la versione e' quella di riferimento e la commessa
    /// lo prevede (impostazione, di default si'), rigenera tutti i documenti. Riepilogo o "".
    /// </summary>
    public static async Task<string> AfterHardware(ProjectVersion version, JobState? job)
    {
        try
        {
            Commessa? c = AppServices.Commesse.Get(version.CommessaId);
            if (c == null || !CommessaSettings.Parse(c.SettingsJson).AutoDocuments || HardwareService.Reference(c)?.Id != version.Id)
            {
                return "";
            }

            if (job != null)
            {
                job.Text = "Documenti in TiaTrackerOut\\Elenchi...";
                job.Indeterminate = true;
            }

            DocExportOutcome? r = await ExportAsync(c.Id, version.Id, Kinds, DocExporter.All, notify: false);
            if (r == null)
            {
                return "";
            }

            int renamed = r.All.Count(f => f.Renamed);
            return r.Ok
                ? "documenti aggiornati in Elenchi" + (renamed > 0 ? " (" + renamed + " file aperti salvati con un altro nome)" : "")
                : "documenti non tutti scritti: vedere il log";
        }
        catch (Exception ex)
        {
            Log.Error("Documenti automatici", ex);
            return "documenti non generati: " + ex.Message;
        }
    }

    public static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
    }

    public static void OpenFile(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Notify.Warning("Apertura non riuscita", Path.GetFileName(path) + ": " + ex.Message);
        }
    }

    public static void ShowInExplorer(string path) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });

    private static void Report(DocExportOutcome r, DocBundle b)
    {
        List<DocFileResult> ok = r.All.Where(f => f.Ok).ToList();
        List<DocFileResult> failed = r.All.Where(f => !f.Ok).ToList();
        List<DocFileResult> renamed = ok.Where(f => f.Renamed).ToList();
        string what = string.Join(", ", r.Written.Where(w => w.Files.Any(f => f.Ok)).Select(w => DocKinds.Title(w.Kind)));
        string formats = string.Join(", ", ok.Select(f => DocExporter.Name(f.Format)).Distinct());
        NoticeAction open = new("Apri cartella", () => OpenFolder(r.Folder));
        NoticeAction? single = ok.Count == 1 ? new NoticeAction("Apri file", () => OpenFile(ok[0].Path)) : null;
        NoticeAction[] actions = single == null ? new[] { open } : new[] { single, open };

        if (failed.Count > 0)
        {
            Notify.Error("Documenti: " + failed.Count + " file non scritti",
                string.Join("\n", failed.Select(f => Path.GetFileName(f.Path) + ": " + f.Error)), actions);
        }
        else if (renamed.Count > 0)
        {
            Notify.Warning("File aperti in un altro programma",
                "Salvati con un altro nome: " + string.Join(", ", renamed.Select(f => Path.GetFileName(f.Path))) +
                ". Chiudere i file e rigenerare per sostituirli.", actions);
        }
        else if (ok.Count > 0)
        {
            Notify.Success("Documenti generati", what + " (" + formats + ")" + (b.Version != null ? " · " + b.Version.Label : "") +
                                                 " in " + ShortPath(r.Folder), actions);
        }
    }

    private static string ShortPath(string folder)
    {
        string[] parts = folder.TrimEnd(Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar);
        return parts.Length <= 2 ? folder : string.Join("\\", parts[^2..]);
    }

    /// <summary>"oggi 14:30", "ieri 09:12", "04/10/2026 14:30".</summary>
    public static string When(DateTime utc)
    {
        DateTime local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        string time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return local.Date == DateTime.Today ? "oggi " + time
            : local.Date == DateTime.Today.AddDays(-1) ? "ieri " + time
            : local.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
    }
}
