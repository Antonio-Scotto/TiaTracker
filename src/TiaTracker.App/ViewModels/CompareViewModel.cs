using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TiaTracker.App.Services;
using TiaTracker.App.Views;
using TiaTracker.Core.Compare;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Reconcile;
using TiaTracker.Core.Scanning;
using TiaTracker.Core.Snapshots;

namespace TiaTracker.App.ViewModels;

/// <summary>Uno snapshot nelle tendine Base/Target, con la versione a cui appartiene.</summary>
public sealed class SnapshotChoice
{
    public SnapshotChoice(Snapshot s, ProjectVersion v)
    {
        Snapshot = s;
        Version = v;
    }

    public Snapshot Snapshot { get; }

    public ProjectVersion Version { get; }

    public string Display => Version.Label + " · " + Snapshot.PlcName + " · " +
                             Snapshot.CreatedUtc.ToLocalTime().ToString("dd/MM/yy HH:mm", CultureInfo.InvariantCulture) + " · " +
                             (Snapshot.Source == SnapshotSources.Attach ? "TIA aperto" : Snapshot.Source) +
                             (Snapshot.ProjectModified == true ? " (non salvato)" : "");

    public override string ToString() => Display;
}

public sealed class CompareRow
{
    public CompareRow(CompareEntry entry, bool child)
    {
        Entry = entry;
        Indent = child ? new Thickness(22, 0, 0, 0) : new Thickness(0);
    }

    public CompareEntry Entry { get; }

    public Thickness Indent { get; }

    public string Status => Entry.StatusText;

    public string Name => Entry.OldName != null ? Entry.Name + "  ← " + Entry.OldName : Entry.Name;

    public string Kind => Entry.Kind;

    public string Facets => Entry.FacetsText + (Entry.Moved && Entry.Status != CompareStatus.Moved ? (Entry.FacetsText.Length > 0 ? ", " : "") + "spostato" : "");

    public string? Note => Entry.ParentName != null ? "derivato da " + Entry.ParentName
        : Entry.Similarity is double s and < 1 ? "somiglianza " + s.ToString("P0", CultureInfo.InvariantCulture)
        : Entry.Status == CompareStatus.Moved ? (Entry.Base?.GroupPath + " → " + Entry.Target?.GroupPath)
        : Entry.Note;
}

public sealed partial class ReconcileRowVm : ObservableObject
{
    public ReconcileRowVm(ReconcileRow row)
    {
        Row = row;
    }

    public ReconcileRow Row { get; }

    public string Status => Row.StatusText;

    public string Block => Row.BlockName;

    public string Changes => Row.ChangesText;

    public string? Note => Row.Note;

    public string? What => Row.Entry?.StatusText;
}

public sealed partial class ProposalVm : ObservableObject
{
    public ProposalVm(StateProposal p)
    {
        Proposal = p;
        Apply = true;
    }

    public StateProposal Proposal { get; }

    [ObservableProperty]
    public partial bool Apply { get; set; }

    public string Text => Proposal.Change.Title + ": " +
                          (Proposal.Current.HasValue ? ChangeStates.Italian(Proposal.Current.Value) : "non associata") + " → " +
                          ChangeStates.Italian(Proposal.Proposed) + (Proposal.Proposed == ChangeState.Compiled ? " (non salvata)" : "") +
                          "  (" + Proposal.Reason + ")";
}

/// <summary>
/// Scheda Confronta: differenze reali fra due snapshot, diff affiancato e
/// riconciliazione con le modifiche dichiarate sulla versione target.
/// </summary>
public sealed partial class CompareViewModel : ObservableObject
{
    private readonly VersionDetailViewModel _owner;
    private List<CompareEntry> _entries = new();
    private long? _runId;
    private Snapshot? _targetSnapshot;
    private ProjectVersion? _baseVersion;

    public CompareViewModel(VersionDetailViewModel owner)
    {
        _owner = owner;
    }

    public ObservableCollection<SnapshotChoice> BaseOptions { get; } = new();

    public ObservableCollection<SnapshotChoice> TargetOptions { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCompareCommand))]
    public partial SnapshotChoice? Base { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCompareCommand))]
    public partial SnapshotChoice? Target { get; set; }

    public ObservableCollection<CompareRow> Rows { get; } = new();

    [ObservableProperty]
    public partial CompareRow? Selected { get; set; }

    public IReadOnlyList<string> Views { get; } = new[] { "Cambiati", "Ricompilati / reimportati", "Non confrontabili", "Invariati", "Tutti" };

    [ObservableProperty]
    public partial string View { get; set; } = "Cambiati";

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial List<DiffRow> DiffRows { get; set; } = new();

    [ObservableProperty]
    public partial bool OnlyChanges { get; set; } = true;

    [ObservableProperty]
    public partial string DiffTitle { get; set; } = "";

    [ObservableProperty]
    public partial string UnitsText { get; set; } = "";

    public ObservableCollection<ReconcileRowVm> Reconcile { get; } = new();

    [ObservableProperty]
    public partial ReconcileRowVm? SelectedReconcile { get; set; }

    public ObservableCollection<ProposalVm> Proposals { get; } = new();

    [ObservableProperty]
    public partial string ReconcileSummary { get; set; } = "";

    private List<DiffRow> _fullDiff = new();

    partial void OnViewChanged(string value) => ApplyFilter();

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnSelectedChanged(CompareRow? value) => LoadDiff();

    partial void OnOnlyChangesChanged(bool value) => DiffRows = OnlyChanges ? DiffBuilder.OnlyChanges(_fullDiff) : _fullDiff;

    partial void OnTargetChanged(SnapshotChoice? value) => FillBaseOptions();

    public void Reload()
    {
        long? keepTarget = Target?.Snapshot.Id;
        long? keepBase = Base?.Snapshot.Id;
        List<ProjectVersion> versions = AppServices.Versions.ByCommessa(_owner.Version.CommessaId);
        Dictionary<long, ProjectVersion> byId = versions.ToDictionary(v => v.Id);
        _allChoices = AppServices.Snapshots.ByCommessa(_owner.Version.CommessaId)
            .Where(s => byId.ContainsKey(s.VersionId))
            .Select(s => new SnapshotChoice(s, byId[s.VersionId]))
            .ToList();

        TargetOptions.Clear();
        foreach (SnapshotChoice c in _allChoices.Where(c => c.Version.Id == _owner.Version.Id))
        {
            TargetOptions.Add(c);
        }

        Target = TargetOptions.FirstOrDefault(t => t.Snapshot.Id == keepTarget) ?? TargetOptions.FirstOrDefault();
        if (keepBase != null)
        {
            Base = BaseOptions.FirstOrDefault(b => b.Snapshot.Id == keepBase) ?? Base;
        }

        if (TargetOptions.Count == 0)
        {
            Summary = "Nessuno snapshot di questa versione: crearne uno (scheda Snapshot) o usare \"Nuovo snapshot e confronta\".";
        }
    }

    private List<SnapshotChoice> _allChoices = new();

    /// <summary>Base proposta: stesso PLC, versione precedente piu' vicina, snapshot piu' recente.</summary>
    private void FillBaseOptions()
    {
        long? keep = Base?.Snapshot.Id;
        BaseOptions.Clear();
        string? plc = Target?.Snapshot.PlcName;
        foreach (SnapshotChoice c in _allChoices.Where(c => Target == null || (c.Snapshot.Id != Target.Snapshot.Id &&
                                                                                string.Equals(c.Snapshot.PlcName, plc, StringComparison.OrdinalIgnoreCase)))
                     .OrderByDescending(c => c.Version.SortKey, StringComparer.Ordinal)
                     .ThenByDescending(c => c.Snapshot.CreatedUtc))
        {
            BaseOptions.Add(c);
        }

        Base = BaseOptions.FirstOrDefault(b => b.Snapshot.Id == keep)
               ?? BaseOptions.FirstOrDefault(b => string.CompareOrdinal(b.Version.SortKey, _owner.Version.SortKey) < 0)
               ?? BaseOptions.FirstOrDefault();
    }

    /// <summary>Base = lo snapshot piu' recente di quella versione (stesso PLC del target) e confronto subito.</summary>
    public bool SelectBaseFromVersion(long versionId)
    {
        SnapshotChoice? choice = BaseOptions.FirstOrDefault(b => b.Version.Id == versionId);
        if (choice == null)
        {
            return false;
        }

        Base = choice;
        if (RunCompareCommand.CanExecute(null))
        {
            RunCompareCommand.Execute(null);
        }

        return true;
    }

    private bool CanCompare() => Base != null && Target != null;

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private void RunCompare()
    {
        if (Base == null || Target == null)
        {
            return;
        }

        List<SnapshotItem> baseItems = AppServices.Snapshots.Items(Base.Snapshot.Id);
        List<SnapshotItem> targetItems = AppServices.Snapshots.Items(Target.Snapshot.Id);
        _entries = SnapshotComparer.Compare(baseItems, targetItems, CanonicalLines);
        _targetSnapshot = Target.Snapshot;
        _baseVersion = Base.Version;

        Dictionary<CompareStatus, int> counts = _entries.GroupBy(e => e.Status).ToDictionary(g => g.Key, g => g.Count());
        int Count(CompareStatus s) => counts.TryGetValue(s, out int n) ? n : 0;
        Summary = $"{Base.Version.Label} → {Target.Version.Label} ({Target.Snapshot.PlcName}): " +
                  $"{Count(CompareStatus.Added)} aggiunti, {Count(CompareStatus.Removed)} rimossi, {Count(CompareStatus.Modified)} modificati, " +
                  $"{Count(CompareStatus.Moved)} spostati, {Count(CompareStatus.Renamed)} rinominati?, " +
                  $"{Count(CompareStatus.Recompiled) + Count(CompareStatus.Reimported)} solo ricompilati/reimportati, " +
                  $"{Count(CompareStatus.Unavailable)} non confrontabili, {Count(CompareStatus.Unchanged)} invariati";
        if (Target.Snapshot.ProjectModified == true)
        {
            Summary += "  ·  target con modifiche NON salvate";
        }

        if (Base.Snapshot.ProjectModified == true)
        {
            Summary += "  ·  base con modifiche NON salvate";
        }

        _runId = AppServices.Compares.SaveRun(Base.Snapshot.Id, Target.Snapshot.Id, _entries, Summary);
        ApplyFilter();
        ReconcileResult? reconcile = RunReconcile();

        ReportPath = OutputService.SaveCompareReport(_owner.Version.CommessaId, Base.Version.Label, Target.Version.Label, Target.Snapshot.PlcName,
            Core.Output.Reports.Compare("Confronto " + Base.Display + " → " + Target.Display, Summary, _entries, reconcile));
    }

    /// <summary>Report del confronto in TiaTrackerOut\Confronti.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenReportCommand))]
    public partial string? ReportPath { get; set; }

    private bool HasReport() => ReportPath != null && File.Exists(ReportPath);

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void OpenReport()
    {
        if (ReportPath != null)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + ReportPath + "\"") { UseShellExecute = true });
        }
    }

    private IReadOnlyList<string>? CanonicalLines(SnapshotItem item)
    {
        string? xml = AppServices.Content.GetText(item.XmlRef);
        return xml == null ? null : XmlCanonicalizer.ToText(XmlCanonicalizer.Canonicalize(xml)).Split('\n');
    }

    private void ApplyFilter()
    {
        string f = Filter.Trim();
        Rows.Clear();
        IEnumerable<CompareEntry> visible = _entries.Where(e => View switch
        {
            "Cambiati" => e.IsChange,
            "Ricompilati / reimportati" => e.Status is CompareStatus.Recompiled or CompareStatus.Reimported,
            "Non confrontabili" => e.Status == CompareStatus.Unavailable,
            "Invariati" => e.Status == CompareStatus.Unchanged,
            _ => true,
        }).Where(e => f.Length == 0 || e.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                      (e.OldName?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false));

        // I derivati sotto il padre.
        List<CompareEntry> list = visible.ToList();
        ILookup<string?, CompareEntry> children = list.Where(e => e.ParentName != null).ToLookup(e => e.ParentName, StringComparer.OrdinalIgnoreCase);
        HashSet<CompareEntry> placed = new();
        foreach (CompareEntry e in list.Where(e => e.ParentName == null))
        {
            Rows.Add(new CompareRow(e, false));
            placed.Add(e);
            foreach (CompareEntry child in children[e.Name])
            {
                Rows.Add(new CompareRow(child, true));
                placed.Add(child);
            }
        }

        foreach (CompareEntry orphan in list.Where(e => !placed.Contains(e)))
        {
            Rows.Add(new CompareRow(orphan, true));
        }
    }

    private void LoadDiff()
    {
        CompareEntry? e = Selected?.Entry;
        if (e == null)
        {
            _fullDiff = new List<DiffRow>();
            DiffRows = _fullDiff;
            DiffTitle = "";
            UnitsText = "";
            return;
        }

        string? leftScl = AppServices.Content.GetText(e.Base?.SclRef);
        string? rightScl = AppServices.Content.GetText(e.Target?.SclRef);
        bool useScl = (leftScl != null || e.Base == null) && (rightScl != null || e.Target == null);

        List<SourceLine> left, right;
        if (useScl)
        {
            left = SclNormalizer.Lines(leftScl);
            right = SclNormalizer.Lines(rightScl);
            DiffTitle = e.Name + " · sorgente SCL (spazi e indentazione ignorati)";
        }
        else
        {
            left = SclNormalizer.XmlLines(Canonical(e.Base));
            right = SclNormalizer.XmlLines(Canonical(e.Target));
            string? language = e.Base?.Language ?? e.Target?.Language;
            DiffTitle = e.Name + " · XML canonico" + (language != null ? " (" + language + ")" : "");
        }

        UnitsText = e.Units.Count == 0 ? "" :
            "Reti cambiate: " + string.Join("; ", e.Units.Select(u => "rete " + u.Index + (string.IsNullOrEmpty(u.Title) ? "" : " \"" + u.Title + "\"") + " " + u.What));
        _fullDiff = DiffBuilder.Build(left, right);
        DiffRows = OnlyChanges && e.Status is CompareStatus.Modified or CompareStatus.Renamed ? DiffBuilder.OnlyChanges(_fullDiff) : _fullDiff;
    }

    private static string? Canonical(SnapshotItem? item)
    {
        string? xml = AppServices.Content.GetText(item?.XmlRef);
        if (xml == null)
        {
            return null;
        }

        try
        {
            return XmlCanonicalizer.ToText(XmlCanonicalizer.Canonicalize(xml));
        }
        catch (System.Xml.XmlException)
        {
            return xml;
        }
    }

    // ---------- riconciliazione ----------

    private ReconcileResult? RunReconcile()
    {
        Reconcile.Clear();
        Proposals.Clear();
        if (_targetSnapshot == null)
        {
            return null;
        }

        List<Change> changes = AppServices.Changes.ByCommessa(_owner.Version.CommessaId);
        List<ReconcileLinkInfo> links = AppServices.Compares.Links(_owner.Version.Id);
        ReconcileResult r = Reconciler.Reconcile(_entries, changes, _owner.Version.Id, _baseVersion?.Id, links, _targetSnapshot);
        foreach (ReconcileRow row in r.Rows)
        {
            Reconcile.Add(new ReconcileRowVm(row));
        }

        foreach (StateProposal p in r.Proposals)
        {
            Proposals.Add(new ProposalVm(p));
        }

        int Count(ReconcileStatus s) => r.Rows.Count(x => x.Status == s);
        ReconcileSummary = $"{Count(ReconcileStatus.Confirmed)} confermate, {Count(ReconcileStatus.DeclaredNotFound)} dichiarate non trovate, " +
                           $"{Count(ReconcileStatus.AlreadyInBase)} gia' nella base, {Count(ReconcileStatus.Undeclared)} non dichiarate, " +
                           $"{Count(ReconcileStatus.Ambiguous)} ambigue, {Count(ReconcileStatus.Ignored)} ignorate";
        return r;
    }

    /// <summary>
    /// Assegna un blocco cambiato ma non dichiarato a una modifica della commessa: diventa
    /// uno dei suoi blocchi e, se la modifica non era su questa versione, ci viene associata.
    /// Le modifiche nuove si creano nella commessa, non da qui.
    /// </summary>
    [RelayCommand]
    private void Assign(ReconcileRowVm? row)
    {
        row ??= SelectedReconcile;
        if (row == null)
        {
            return;
        }

        // Prima quelle gia' su questa versione.
        List<Change> candidates = AppServices.Changes.ByCommessa(_owner.Version.CommessaId)
            .OrderBy(c => c.Versions.Any(v => v.VersionId == _owner.Version.Id) ? 0 : 1).ToList();
        if (candidates.Count == 0)
        {
            Notify.Info("Assegna", "La commessa non ha ancora modifiche: si creano nella scheda Modifiche della commessa.");
            return;
        }

        bool unsaved = _targetSnapshot?.Source == SnapshotSources.Attach && _targetSnapshot.ProjectModified == true;
        PickChangesWindow dialog = new(candidates, unsaved ? ChangeState.Compiled : ChangeState.Saved,
            "Stato su " + _owner.Version.Label + " (se non c'era)", _owner.Version.Id)
        {
            Owner = Application.Current.MainWindow,
            Title = "Assegna " + row.Block + " a una modifica",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        foreach (Change pick in dialog.Selected)
        {
            Change c = AppServices.Changes.Get(pick.Id)!;
            bool save = false;
            if (!c.Blocks.Any(b => b.BlockName.Equals(row.Block, StringComparison.OrdinalIgnoreCase)))
            {
                c.Blocks.Add(new ChangeBlock { BlockName = row.Block, Family = row.Row.Family, Action = ActionOf(row.Row.Entry) });
                save = true;
            }

            if (c.Versions.All(v => v.VersionId != _owner.Version.Id))
            {
                c.Versions.Add(new ChangeVersion { VersionId = _owner.Version.Id, State = dialog.State, EvidenceRunId = _runId });
                save = true;
            }

            if (save)
            {
                AppServices.Changes.Save(c, "blocco assegnato dalla riconciliazione");
            }

            AppServices.Compares.SetLink(_owner.Version.Id, row.Row.Family ?? "block", row.Block, c.Id, ReconcileDecisions.Assign);
        }

        RunReconcile();
        _owner.Refresh();
    }

    [RelayCommand]
    private void Ignore(ReconcileRowVm? row)
    {
        row ??= SelectedReconcile;
        if (row == null || row.Row.Status != ReconcileStatus.Undeclared)
        {
            return;
        }

        AppServices.Compares.SetLink(_owner.Version.Id, row.Row.Family ?? "block", row.Block, null, ReconcileDecisions.Ignore);
        RunReconcile();
    }

    [RelayCommand]
    private void Undo(ReconcileRowVm? row)
    {
        row ??= SelectedReconcile;
        if (row == null)
        {
            return;
        }

        AppServices.Compares.RemoveLinks(_owner.Version.Id, row.Row.Family ?? "block", row.Block);
        RunReconcile();
    }

    [RelayCommand]
    private void ApplyProposals()
    {
        int n = 0;
        foreach (ProposalVm p in Proposals.Where(p => p.Apply))
        {
            AppServices.Changes.SetVersionState(p.Proposal.Change.Id, _owner.Version.Id, p.Proposal.Proposed,
                "da confronto: " + p.Proposal.Reason, _runId);
            n++;
        }

        if (n > 0)
        {
            _owner.Main.StatusText = n + " stati aggiornati dal confronto";
            RunReconcile();
            _owner.Refresh();
        }
    }

    /// <summary>Snapshot nuovo della versione (da TIA aperto se aperta, altrimenti da file) e confronto con la base.</summary>
    [RelayCommand]
    private async Task SnapshotAndCompare()
    {
        bool open = _owner.Node.Status.Open.Kind == OpenKind.Open;
        if (!open && !_owner.Version.HasFolder)
        {
            Notify.Info("Confronta", "La versione non ha una cartella: fare lo snapshot dal backup o dal rar nella scheda Snapshot.");
            return;
        }

        string how = open
            ? "La versione e' aperta in TIA: lo snapshot si fa dal TIA aperto (attach), incluse le modifiche non salvate."
            : "La versione e' chiusa: lo snapshot si fa su una copia, aperta da TIA senza interfaccia.";
        if (MessageBox.Show(how + "\n\nProcedere?", "Nuovo snapshot e confronta", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        long? keepBase = Base?.Snapshot.Id;
        await _owner.Snapshots.RunFromCompare(open);
        Reload();
        if (keepBase != null)
        {
            Base = BaseOptions.FirstOrDefault(b => b.Snapshot.Id == keepBase) ?? Base;
        }

        if (CanCompare())
        {
            RunCompare();
        }
    }

    private static string? ActionOf(CompareEntry? e) => e?.Status switch
    {
        CompareStatus.Added => "nuovo",
        CompareStatus.Removed => "elimina",
        CompareStatus.Modified or CompareStatus.Renamed => "modifica",
        _ => null,
    };
}
