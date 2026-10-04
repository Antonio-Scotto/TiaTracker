using System.Diagnostics;
using System.Windows;
using TiaTracker.App.Services;
using TiaTracker.App.Views;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Scanning;
using TiaTracker.Data;

namespace TiaTracker.App.ViewModels;

/// <summary>
/// Tutto cio' che si fa su una versione, dal menu tasto destro, dal pulsante
/// "Stato" e dalla scheda della versione: stato, carichi sui PLC, snapshot,
/// file. Ogni cambiamento aggiorna albero, dettaglio e TiaTrackerOut.
/// </summary>
public sealed class VersionActions
{
    public VersionActions(MainViewModel main, VersionNode node)
    {
        Main = main;
        Node = node;
    }

    public MainViewModel Main { get; }

    public VersionNode Node { get; }

    public ProjectVersion Version => Node.Version;

    public ScanRoot? Root => Node.Parent.RootOf(Version);

    public string? FolderFull => Version.HasFolder ? ScanService.Full(Root, Version.FolderRel) : null;

    public string? ProjectFileFull => ScanService.Full(Root, Version.ProjectFileRel);

    public string? RarFull => Version.HasRar ? ScanService.Full(Root, Version.RarRel) : null;

    public string? BackupFull => Version.HasBackup ? ScanService.Full(Root, Version.BackupRel) : null;

    public bool HasProject => ProjectFileFull != null && File.Exists(ProjectFileFull);

    public bool IsOpenInTia => Node.Status.Open.Kind == OpenKind.Open;

    public bool CanSnapshotFromFolder => Version.HasFolder && !IsOpenInTia && Version.ProjectFileRel != null;

    // ---------- stato ----------

    public async Task SetState(VersionState state)
    {
        if (state == VersionState.Caricata)
        {
            await MarkLoaded();
            return;
        }

        if (Version.State == state && state != VersionState.InCollaudo)
        {
            return;
        }

        string? note = null;
        if (state == VersionState.InCollaudo)
        {
            note = TextPromptWindow.Ask("In collaudo", "Nota del collaudo, facoltativa (es. FAT, SAT 12/10):", Version.StateNote ?? "FAT");
            if (note == null)
            {
                return;
            }
        }

        bool endLoads = false;
        if (Node.Status.OnPlc && VersionStates.IsRetired(state))
        {
            MessageBoxResult answer = MessageBox.Show(
                Version.Label + " risulta ancora sul PLC (" + Node.Status.LoadsText + ").\n\nTogliere anche il carico dal PLC?\n" +
                "Si' = non e' piu' sul PLC.   No = resta segnata sul PLC.",
                VersionStates.Italian(state), MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer == MessageBoxResult.Cancel)
            {
                return;
            }

            endLoads = answer == MessageBoxResult.Yes;
        }

        try
        {
            if (endLoads)
            {
                AppServices.Versions.EndLoads(Version.Id, null, "stato " + VersionStates.Italian(state));
            }

            AppServices.Versions.SetState(Version.Id, state, note);
            AfterChange(Version.Label + ": " + (state == VersionState.None ? "nessuno stato" : VersionStates.Italian(state)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            Log.Error("Stato versione", ex);
            Notify.Error("Stato non cambiato", ex.Message);
        }
    }

    /// <summary>
    /// Questa diventa la versione su cui si lavora: In lavoro, e le altre In lavoro
    /// piu' vecchie e non sui PLC diventano Vecchie (una sola versione di riferimento).
    /// </summary>
    public async Task SetAsWorkVersion()
    {
        List<VersionNode> previous = Node.Parent.Versions
            .Where(x => x != Node && x.Version.State == VersionState.InLavoro && !x.Status.OnPlc &&
                        string.CompareOrdinal(x.Version.SortKey, Version.SortKey) < 0)
            .ToList();
        foreach (VersionNode p in previous)
        {
            AppServices.Versions.SetState(p.Version.Id, VersionState.Vecchia, "superata da " + Version.Label);
        }

        await SetState(VersionState.InLavoro);
        if (previous.Count > 0)
        {
            Main.StatusText = Version.Label + " in lavoro; " + string.Join(", ", previous.Select(p => p.Version.Label)) + " vecchia";
        }
    }

    /// <summary>Caricata: PLC, data e nota; la precedente sugli stessi PLC diventa Vecchia; snapshot di prova proposto.</summary>
    public async Task MarkLoaded()
    {
        List<VersionLoad> current = AppServices.Versions.CurrentLoads(Version.CommessaId);
        List<string> known = AppServices.Versions.KnownPlcs(Version.CommessaId)
            .Concat(AppServices.Snapshots.ByCommessa(Version.CommessaId).Select(s => s.PlcName).Where(p => !string.IsNullOrWhiteSpace(p)).Cast<string>())
            .Concat(current.Where(l => l.Plc.Length > 0).Select(l => l.Plc))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Dictionary<long, string> labels = Node.Parent.Versions.ToDictionary(v => v.Version.Id, v => v.Label);

        LoadedWindow dialog = new(Version, known, current, labels, IsOpenInTia) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        List<VersionRepository.Replaced> replaced;
        try
        {
            replaced = AppServices.Versions.RecordLoad(Version.Id, dialog.Plcs, dialog.LoadedUtc, dialog.Note);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            Log.Error("Caricata", ex);
            Notify.Error("Carico non registrato", ex.Message);
            return;
        }

        string plcs = string.Join(", ", dialog.Plcs.Select(p => p.Length == 0 ? "PLC" : p));
        AfterChange(Version.Label + " caricata su " + plcs);
        List<string> old = replaced.Where(r => r.BecameOld).Select(r => r.Label + " → Vecchia").ToList();
        string? detail = old.Count > 0 ? string.Join(", ", old) : replaced.Count > 0 ? "Tolta dai PLC: " + string.Join(", ", replaced.Select(r => r.Label)) : null;

        if (dialog.TakeProofSnapshot && IsOpenInTia)
        {
            Notify.Success(Version.Label + " caricata su " + plcs, detail);
            await SnapshotFromOpen(proof: true);
        }
        else if (CanSnapshotFromFolder)
        {
            Notify.Success(Version.Label + " caricata su " + plcs,
                (detail != null ? detail + ". " : "") + "Per avere la prova di cosa c'e' sul PLC si puo' fare uno snapshot dalla cartella (1-2 minuti).",
                new NoticeAction("Snapshot di prova", () => _ = SnapshotFromFolder(proof: true)));
        }
        else
        {
            Notify.Success(Version.Label + " caricata su " + plcs, detail);
        }
    }

    public void EndLoads()
    {
        if (!Node.Status.OnPlc)
        {
            return;
        }

        if (MessageBox.Show(Version.Label + " non e' piu' sul PLC (" + Node.Status.LoadsText + ")?\nLo stato resta " +
                            (Node.Status.ChipText.Length > 0 ? Node.Status.ChipText : "invariato") + ".",
                "Togli dal PLC", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        AppServices.Versions.EndLoads(Version.Id, null, "tolta dal PLC a mano");
        AfterChange(Version.Label + ": tolta dal PLC");
    }

    // ---------- snapshot ----------

    public async Task<SnapshotService.Outcome?> SnapshotFromOpen(bool proof = false, string? title = null)
    {
        SnapshotService.Outcome? outcome = null;
        await JobRunner.RunAsync(title ?? (proof ? "Snapshot di prova da TIA aperto" : "Snapshot da TIA aperto"), async (job, ct) =>
        {
            outcome = await SnapshotService.FromOpenTia(Version, ProjectFileFull, job, ct);
            return ToJob(outcome);
        });
        AfterSnapshot(outcome, proof);
        return outcome;
    }

    public async Task<SnapshotService.Outcome?> SnapshotFromFolder(bool proof = false, string? title = null)
    {
        string? folder = FolderFull;
        if (folder == null || Version.ProjectFileRel == null)
        {
            Notify.Warning("Snapshot dalla cartella", "La versione non ha una cartella progetto.");
            return null;
        }

        SnapshotService.Outcome? outcome = null;
        await JobRunner.RunAsync(title ?? (proof ? "Snapshot di prova dalla cartella" : "Snapshot dalla cartella"), async (job, ct) =>
        {
            outcome = await SnapshotService.FromFile(Version, folder, job, ct);
            return ToJob(outcome);
        });
        AfterSnapshot(outcome, proof);
        return outcome;
    }

    /// <summary>Da un archivio (zip di backup TIA o rar): estrazione in una cartella di lavoro e apertura senza interfaccia.</summary>
    public async Task<SnapshotService.Outcome?> SnapshotFromArchive(string archive, string source, string? title = null)
    {
        SnapshotService.Outcome? outcome = null;
        await JobRunner.RunAsync(title ?? "Snapshot da " + Path.GetFileName(archive), async (job, ct) =>
        {
            outcome = await SnapshotService.FromArchive(Version, archive, source, job, ct);
            return ToJob(outcome);
        });
        AfterSnapshot(outcome, false);
        return outcome;
    }

    /// <summary>Lo zip piu' recente nella cartella di backup di TIA (l'orario vero e' quello del file), o null.</summary>
    public string? NewestBackupZip()
    {
        string? backup = BackupFull;
        if (backup == null || !Directory.Exists(backup))
        {
            return null;
        }

        return new DirectoryInfo(backup).EnumerateFiles("*.zip", SearchOption.AllDirectories)
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
    }

    /// <summary>Come si farebbe lo snapshot ora: dal TIA aperto, dalla cartella, dal backup o dal rar (null = impossibile).</summary>
    public string? BestSnapshotSource() =>
        IsOpenInTia ? "dal TIA aperto (pochi minuti)"
        : CanSnapshotFromFolder ? "dalla cartella, aperta da TIA senza interfaccia (qualche minuto)"
        : Version.HasBackup ? "dal backup .zip piu' recente"
        : RarFull != null ? "dal .rar"
        : null;

    /// <summary>Snapshot (blocchi, hardware, rete e tag) dalla fonte migliore disponibile.</summary>
    public async Task<SnapshotService.Outcome?> SnapshotBest(string? title = null)
    {
        if (IsOpenInTia)
        {
            return await SnapshotFromOpen(title: title);
        }

        if (CanSnapshotFromFolder)
        {
            return await SnapshotFromFolder(title: title);
        }

        string? zip = NewestBackupZip();
        if (zip != null)
        {
            return await SnapshotFromArchive(zip, SnapshotSources.Backup, title);
        }

        if (RarFull != null && File.Exists(RarFull))
        {
            return await SnapshotFromArchive(RarFull, SnapshotSources.Rar, title);
        }

        Notify.Warning("Snapshot", Version.Label + ": nessuna cartella progetto o archivio da cui fare l'export.");
        return null;
    }

    /// <summary>Lista IP, hardware, IO e registro di questa versione in DOCX, PDF e CSV (Elenchi\ o Elenchi\&lt;versione&gt;\).</summary>
    public async Task GenerateDocuments()
    {
        await DocumentService.ExportAsync(Version.CommessaId, Version.Id, DocumentService.Kinds.ToArray(), Export.DocExporter.All.ToArray());
        if (Main.Detail is CommessaDetailViewModel cd && cd.Node == Node.Parent)
        {
            cd.Docs.Invalidate();
        }
    }

    /// <summary>Solo hardware, rete e tag (per Lista IP/HW/IO): pochi secondi dal TIA aperto, 1-2 minuti dalla cartella.</summary>
    public async Task ReadHardware(bool forceIpSync = false)
    {
        if (!IsOpenInTia && !CanSnapshotFromFolder)
        {
            Notify.Warning("Hardware da TIA", Version.Label + " non e' aperta in TIA e non ha una cartella progetto da aprire.");
            return;
        }

        string? folder = FolderFull;
        await JobRunner.RunAsync("Hardware e rete da TIA (" + Version.Label + ")", (job, ct) => IsOpenInTia
            ? HardwareService.FromOpenTia(Version, ProjectFileFull, job, ct, forceIpSync)
            : HardwareService.FromFolder(Version, folder!, job, ct, forceIpSync));
        Main.Reload(Node.Parent);
    }

    private static JobOutcome ToJob(SnapshotService.Outcome r) =>
        r.Ok ? JobOutcome.Done(r.Message)
        : r.NeedsDiagnostics ? JobOutcome.Failed(r.Message, JobRunner.DiagnosticsAction())
        : JobOutcome.Failed(r.Message);

    private void AfterSnapshot(SnapshotService.Outcome? outcome, bool proof)
    {
        if (outcome is { Ok: true } && proof && outcome.SnapshotIds.Count > 0)
        {
            AppServices.Versions.AttachProofSnapshot(Version.Id, outcome.SnapshotIds[0]);
            AppServices.Versions.AddEvent(Version.Id, "prova", "Snapshot di prova del carico registrato");
        }

        Main.Reload(Node.Parent);
    }

    /// <summary>Apre la scheda Confronta con base la versione caricata sul PLC.</summary>
    public void CompareWithLoaded()
    {
        VersionLoad? load = Node.Parent.CurrentLoads.FirstOrDefault(l => l.VersionId != Version.Id);
        if (load == null)
        {
            Notify.Info("Confronta con la caricata", "Nessun'altra versione risulta caricata su un PLC.");
            return;
        }

        Node.IsSelected = true;
        if (Main.Detail is VersionDetailViewModel vd && vd.Node == Node)
        {
            vd.SelectedTab = VersionDetailViewModel.CompareTab;
            if (!vd.Compare.SelectBaseFromVersion(load.VersionId))
            {
                Notify.Info("Confronta con la caricata", "La versione caricata non ha snapshot dello stesso PLC: farne uno prima.");
            }
        }
    }

    // ---------- file ----------

    public void OpenInTia()
    {
        if (!HasProject)
        {
            Notify.Warning("Apri in TIA", "Questa versione non ha una cartella progetto (solo archivio).");
            return;
        }

        if (Node.Status.Open.Kind is OpenKind.Open or OpenKind.OpenOtherPc)
        {
            Notify.Info("Gia' aperta", Node.Status.Open.Describe());
            return;
        }

        VersionFileOps.OpenInTia(ProjectFileFull!);
        AppServices.Versions.AddEvent(Version.Id, "aperta in TIA", ProjectFileFull);
        Main.StatusText = "Apertura di " + Version.ProjectName + " in TIA Portal...";
    }

    public void OpenFolder(string? path = null)
    {
        string? target = path ?? FolderFull ?? BackupFull ?? (RarFull != null ? Path.GetDirectoryName(RarFull) : null);
        if (target == null)
        {
            return;
        }

        if (File.Exists(target))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + target + "\"") { UseShellExecute = true });
        }
        else if (Directory.Exists(target))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + target + "\"") { UseShellExecute = true });
        }
    }

    public void CopyPath()
    {
        string? path = FolderFull ?? RarFull ?? BackupFull;
        if (path == null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(path);
            Main.StatusText = "Percorso copiato: " + path;
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Notify.Warning("Appunti non disponibili", ex.Message);
        }
    }

    /// <summary>Copia della cartella con un nome nuovo: la versione successiva (o un duplicato).</summary>
    public async Task NewVersionFrom()
    {
        if (!HasProject || FolderFull == null || Root == null)
        {
            Notify.Warning("Nuova versione", "Serve la cartella del progetto: una versione solo archivio va prima estratta.");
            return;
        }

        List<ProjectVersion> all = AppServices.Versions.ByCommessa(Version.CommessaId);
        IEnumerable<string> taken = all.Select(v => v.ProjectName)
            .Concat(Directory.GetDirectories(Path.GetDirectoryName(FolderFull)!).Select(Path.GetFileName).Where(n => n != null).Cast<string>());
        string proposed = Core.Output.VersionNaming.Next(Version.ProjectName, taken, DateTime.Today);
        bool open = Node.Status.Open.Kind == OpenKind.Open;
        bool sourceToOld = Version.State is VersionState.None or VersionState.InLavoro && !Node.Status.OnPlc;

        NewVersionWindow dialog = new(Version, proposed, open, sourceToOld) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string name = dialog.NewName;
        string folder = FolderFull!, project = ProjectFileFull!;
        ScanRoot root = Root!;
        await JobRunner.RunAsync("Nuova versione " + name, async (job, ct) =>
        {
            job.Text = "Copia di " + Version.ProjectName + " in " + name + "...";
            await Task.Run(() => VersionFileOps.Duplicate(folder, project, name), ct);
            await ScanService.ScanAsync(root);
            ProjectVersion? created = AppServices.Versions.ByCommessa(Version.CommessaId)
                .FirstOrDefault(v => v.ProjectName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (created == null)
            {
                return JobOutcome.Failed("La copia c'e' ma la scansione non l'ha trovata: controllare la regex delle etichette.");
            }

            AppServices.Versions.AddEvent(created.Id, "creata", "Copia di " + Version.Label + " (" + Version.ProjectName + ")");
            if (dialog.MarkActive)
            {
                AppServices.Versions.SetState(created.Id, VersionState.InLavoro);
            }

            if (dialog.SourceToOld && Version.State != VersionState.Vecchia)
            {
                AppServices.Versions.SetState(Version.Id, VersionState.Vecchia);
            }

            // Le modifiche gia' salvate nella versione di partenza sono nella copia.
            if (dialog.CarryChanges)
            {
                foreach (Change c in AppServices.Changes.ByCommessa(Version.CommessaId))
                {
                    if (c.Versions.Any(v => v.VersionId == Version.Id && v.State == ChangeState.Saved))
                    {
                        AppServices.Changes.SetVersionState(c.Id, created.Id, ChangeState.Saved, "ereditata da " + Version.Label);
                    }
                }
            }

            OutputService.Refresh(Version.CommessaId);
            Main.Reload(Node.Parent);
            VersionNode? node = Node.Parent.Versions.FirstOrDefault(v => v.Version.Id == created.Id);
            if (node != null)
            {
                node.IsSelected = true;
            }

            return JobOutcome.Done("Creata " + name + (dialog.MarkActive ? " (In lavoro)" : ""));
        });
    }

    /// <summary>Cartella nel Cestino; la versione resta con la sua storia, segnata mancante.</summary>
    public void DeleteVersion()
    {
        string? folder = FolderFull;
        if (folder == null || !Directory.Exists(folder) || Root == null)
        {
            Notify.Warning("Elimina versione", "Questa versione non ha una cartella da eliminare.");
            return;
        }

        if (Node.Status.Open.Kind is OpenKind.Open or OpenKind.OpenOtherPc)
        {
            Notify.Warning("Elimina versione", "La versione e' aperta in TIA: chiuderla prima di eliminarla. " + Node.Status.Open.Describe());
            return;
        }

        string warning = Node.Status.OnPlc ? "\n\nATTENZIONE: risulta sul PLC (" + Node.Status.LoadsText + ")." : "";
        string archives = Version.HasRar || Version.HasBackup ? "\nIl .rar e la cartella .backup restano dove sono." : "\nNon ci sono .rar ne' backup di questa versione.";
        if (MessageBox.Show(
                "Spostare nel Cestino la cartella\n" + folder + "?" + archives +
                "\nIn TiaTracker la versione resta con modifiche e storia, segnata come mancante." + warning,
                "Elimina versione " + Version.Label, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            VersionFileOps.Recycle(folder);
            AppServices.Versions.AddEvent(Version.Id, "eliminata", "Cartella spostata nel Cestino: " + folder);
            ScanService.Scan(Root);
            AfterChange(Version.Label + ": cartella nel Cestino");
        }
        catch (IOException ex)
        {
            Log.Error("Elimina versione", ex);
            Notify.Error("Elimina versione", ex.Message);
        }
    }

    /// <summary>Backup .rar della cartella progetto nella cartella di rete della commessa.</summary>
    public async Task BackupRar()
    {
        string? folder = FolderFull;
        if (folder == null || !Directory.Exists(folder))
        {
            Notify.Warning("Backup .rar", "Questa versione non ha una cartella progetto da archiviare.");
            return;
        }

        Commessa c = AppServices.Commesse.Get(Version.CommessaId)!;
        if (string.IsNullOrWhiteSpace(c.BackupDir))
        {
            Microsoft.Win32.OpenFolderDialog dialog = new() { Title = "Cartella di rete per i backup .rar di " + c.Display };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            c.BackupDir = dialog.FolderName;
            AppServices.Commesse.Update(c);
        }

        if (IsOpenInTia &&
            MessageBox.Show("La versione e' aperta in TIA: si archivia l'ultimo salvataggio, le modifiche non salvate restano fuori.\n\nProcedere?",
                "Backup .rar", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        string backupDir = c.BackupDir!;
        await JobRunner.RunAsync("Backup .rar di " + Version.Label, async (job, ct) =>
        {
            string target = await VersionFileOps.BackupRar(folder, Version.ProjectName, backupDir,
                t => Application.Current.Dispatcher.BeginInvoke(() => job.Text = t), ct);
            AppServices.Versions.AddEvent(Version.Id, "backup rar", target);
            Main.Reload(Node.Parent);
            return JobOutcome.Done(target, new NoticeAction("Apri cartella", () => OpenFolder(target)));
        });
    }

    private void AfterChange(string status)
    {
        OutputService.Refresh(Version.CommessaId);
        Main.Reload(Node.Parent);
        Main.StatusText = status;
    }
}
