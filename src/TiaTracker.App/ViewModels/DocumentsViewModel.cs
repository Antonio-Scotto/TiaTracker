using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TiaTracker.App.Services;
using TiaTracker.Core.Documents;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Output;
using TiaTracker.Data;
using TiaTracker.Export;

namespace TiaTracker.App.ViewModels;

/// <summary>Un documento nell'elenco a sinistra: titolo, ultima generazione, freschezza.</summary>
public sealed partial class DocKindVm : ObservableObject
{
    public DocKindVm(string kind, string glyph)
    {
        Kind = kind;
        Glyph = glyph;
    }

    public string Kind { get; }

    public string Title => DocKinds.Title(Kind);

    public string Description => DocumentService.Describe(Kind);

    /// <summary>Glifo Segoe Fluent Icons.</summary>
    public string Glyph { get; }

    public override string ToString() => Title;

    [ObservableProperty]
    public partial string LastText { get; set; } = "Mai generato";

    [ObservableProperty]
    public partial ChipVm? Freshness { get; set; }

    [ObservableProperty]
    public partial string Count { get; set; } = "";

    /// <summary>File dell'ultima generazione ancora presenti su disco.</summary>
    public List<string> LastFiles { get; set; } = new();
}

/// <summary>Una versione nella tendina, con l'export hardware che userebbero i documenti.</summary>
public sealed class DocVersionChoice
{
    public required ProjectVersion Version { get; init; }
    public bool IsReference { get; init; }
    public HwSnapshot? Hardware { get; init; }

    public string Text => Version.Label + (Version.State != VersionState.None ? " · " + VersionStates.Italian(Version.State) : "") +
                          (IsReference ? " · riferimento" : "");

    public string Detail => Hardware == null ? "nessun export da TIA" : "export " + DocumentService.When(Hardware.CreatedUtc);

    public override string ToString() => Text;
}

/// <summary>Una riga dell'anteprima: celle per indice ([0], [1]...), modificabili solo dove ha senso.</summary>
public sealed class DocPreviewRow : INotifyPropertyChanged
{
    private readonly DocumentsViewModel _owner;
    private readonly string?[] _cells;

    public DocPreviewRow(DocumentsViewModel owner, DocRow row, int columns)
    {
        _owner = owner;
        Row = row;
        _cells = new string?[Math.Max(columns, 1)];
        for (int i = 0; i < _cells.Length && i < row.Cells.Count; i++)
        {
            _cells[i] = row.Cells[i];
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DocRow Row { get; }

    public bool IsGroup => Row.Style == DocRowStyle.Group;

    public bool IsMuted => Row.Style == DocRowStyle.Muted;

    public bool IsManual => Row.IsManual;

    public string?[] Cells => _cells;

    public string? this[int index]
    {
        get => index < _cells.Length ? _cells[index] : null;
        set
        {
            if (index >= _cells.Length || string.Equals(_cells[index] ?? "", value ?? "", StringComparison.Ordinal))
            {
                return;
            }

            _cells[index] = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            _owner.OnCellEdited(this, index, value);
        }
    }

    public bool Matches(string filter) => _cells.Any(c => c != null && c.Contains(filter, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Un file della cartella Documenti\ della commessa.</summary>
public sealed record CommessaFileVm(string Path, string Name, string Folder, DateTime Modified, long Size)
{
    public string Info => Modified.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) + " · " + SizeText;

    public string SizeText => Size < 1024 ? Size + " B" : Size < 1024 * 1024 ? (Size / 1024) + " KB" : (Size / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
}

/// <summary>
/// Scheda Documenti: Lista IP, hardware, IO e registro generati dai dati di
/// TIA, con note e righe aggiunte a mano che restano a ogni rigenerazione;
/// sotto, i file liberi della cartella Documenti\ della commessa.
/// </summary>
public sealed partial class DocumentsViewModel : ObservableObject
{
    private readonly CommessaNode _node;
    private readonly MainViewModel _main;
    private readonly HashSet<string> _deletedSince = new(StringComparer.Ordinal);
    private DocBundle? _bundle;
    private List<DocPreviewRow> _allRows = new();
    private bool _loaded;
    private bool _dirty = true;
    private bool _switching;

    public DocumentsViewModel(CommessaNode node, MainViewModel main)
    {
        _node = node;
        _main = main;
        Kinds.Add(new DocKindVm(DocKinds.Ip, "\uE968"));
        Kinds.Add(new DocKindVm(DocKinds.Hardware, "\uE950"));
        Kinds.Add(new DocKindVm(DocKinds.Io, "\uE8FD"));
        Kinds.Add(new DocKindVm(DocKinds.Register, "\uE70B"));
        Kinds.Add(new DocKindVm(DocKinds.Plan, "\uE787"));
        SelectedKind = Kinds[0];
    }

    /// <summary>Le colonne dell'anteprima sono cambiate: la vista ricrea quelle della griglia.</summary>
    public event Action? PreviewColumnsChanged;

    public long CommessaId => _node.Commessa.Id;

    public ObservableCollection<DocKindVm> Kinds { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditRows), nameof(PreviewHint), nameof(PreviewTitle))]
    public partial DocKindVm? SelectedKind { get; set; }

    public ObservableCollection<DocVersionChoice> Versions { get; } = new();

    [ObservableProperty]
    public partial DocVersionChoice? SelectedVersion { get; set; }

    [ObservableProperty]
    public partial string SourceText { get; set; } = "";

    [ObservableProperty]
    public partial ChipVm? SourceChip { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    public ObservableCollection<DocPreviewRow> Rows { get; } = new();

    [ObservableProperty]
    public partial DocPreviewRow? SelectedRow { get; set; }

    public IReadOnlyList<DocColumn> PreviewColumns { get; private set; } = Array.Empty<DocColumn>();

    public int NoteColumn { get; private set; } = -1;

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    public partial string PreviewCount { get; set; } = "";

    [ObservableProperty]
    public partial string? EmptyText { get; set; }

    /// <summary>Note e righe manuali solo dove i dati vengono da TIA (hardware, IO).</summary>
    public bool CanEditRows => SelectedKind?.Kind is DocKinds.Hardware or DocKinds.Io;

    public string PreviewTitle => SelectedKind == null ? "" : SelectedKind.Title;

    public string PreviewHint => SelectedKind?.Kind switch
    {
        DocKinds.Ip => "Anteprima: la Lista IP si modifica nella scheda Rete / IP.",
        DocKinds.Register => "Anteprima: versioni e modifiche si gestiscono nelle schede Modifiche e Matrice.",
        DocKinds.Plan => "Anteprima: il piano si modifica nella scheda Pianificazione.",
        _ => "Colonna Note modificabile e righe aggiunte a mano: restano a ogni rigenerazione da TIA.",
    };

    public string ListsFolder => OutputService.Layout(CommessaId)?.Lists ?? "";

    public ObservableCollection<CommessaFileVm> Files { get; } = new();

    [ObservableProperty]
    public partial CommessaFileVm? SelectedFile { get; set; }

    [ObservableProperty]
    public partial string FilesText { get; set; } = "";

    // ---------- caricamento ----------

    /// <summary>La scheda e' visibile (lo imposta la vista): i dati si caricano solo quando servono.</summary>
    public bool IsShown { get; set; }

    /// <summary>Alla prima apertura della scheda e dopo ogni cambio (snapshot, stati, IP).</summary>
    public void EnsureLoaded()
    {
        if (!_loaded || _dirty)
        {
            _ = ReloadAsync(rebuildVersions: true);
        }
    }

    /// <summary>La commessa e' cambiata: si ricarica alla prossima apertura (subito se la scheda e' aperta).</summary>
    public void Invalidate()
    {
        _dirty = true;
        if (IsShown && _loaded)
        {
            _ = ReloadAsync(rebuildVersions: true);
        }
    }

    private async Task ReloadAsync(bool rebuildVersions)
    {
        if (IsBusy)
        {
            _dirty = true;
            return;
        }

        IsBusy = true;
        try
        {
            _loaded = true;
            _dirty = false;
            if (rebuildVersions)
            {
                RebuildVersions();
            }

            _bundle = await DocumentService.BuildAsync(CommessaId, SelectedVersion?.Version.Id);
            UpdateSource();
            UpdateFreshness();
            ShowPreview();
            LoadFiles();
        }
        catch (Exception ex)
        {
            Log.Error("Documenti: anteprima", ex);
            Status = "Anteprima non disponibile: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }

        if (_dirty)
        {
            await ReloadAsync(rebuildVersions: true);
        }
    }

    private void RebuildVersions()
    {
        long? keep = SelectedVersion?.Version.Id;
        ProjectVersion? reference = HardwareService.Reference(_node.Commessa);
        Dictionary<long, HwSnapshot> hw = AppServices.Hardware.LatestByVersion(CommessaId);
        _switching = true;
        Versions.Clear();
        foreach (ProjectVersion v in _node.Versions.Select(n => n.Version).Where(v => !v.Missing && v.State != VersionState.Scartata)
                     .OrderByDescending(v => v.SortKey, StringComparer.Ordinal))
        {
            Versions.Add(new DocVersionChoice { Version = v, IsReference = v.Id == reference?.Id, Hardware = hw.GetValueOrDefault(v.Id) });
        }

        SelectedVersion = Versions.FirstOrDefault(v => v.Version.Id == keep) ?? Versions.FirstOrDefault(v => v.IsReference) ?? Versions.FirstOrDefault();
        _switching = false;
    }

    partial void OnSelectedVersionChanged(DocVersionChoice? value)
    {
        if (!_switching && _loaded)
        {
            _ = ReloadAsync(rebuildVersions: false);
        }
    }

    partial void OnSelectedKindChanged(DocKindVm? value)
    {
        if (_loaded)
        {
            ShowPreview();
        }
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void UpdateSource()
    {
        HwSnapshot? h = _bundle?.Hardware;
        if (_bundle?.Version == null)
        {
            SourceChip = new ChipVm("Nessuna versione", Tone.Muted);
            SourceText = "La commessa non ha versioni: aggiungere una cartella di scansione nella Panoramica.";
            return;
        }

        if (h == null)
        {
            SourceChip = new ChipVm("Nessun export da TIA", Tone.Warning, "Lista hardware e Lista IO restano vuote finche' non si leggono da TIA");
            SourceText = "Per " + _bundle.Version.Label + " non c'e' ancora un export di hardware, rete e tag: \"Aggiorna da TIA\".";
            return;
        }

        bool saved = h.ProjectModified != true;
        bool stale = _bundle.Version.LastSavedUtc is DateTime savedUtc && savedUtc > h.CreatedUtc.AddMinutes(1);
        SourceChip = !saved ? new ChipVm("Progetto non salvato", Tone.Danger, "Al momento dell'export il progetto in TIA aveva modifiche non salvate")
            : stale ? new ChipVm("Progetto salvato dopo l'export", Tone.Warning, "Il progetto e' stato salvato dopo l'ultimo export: \"Aggiorna da TIA\"")
            : new ChipVm("Export aggiornato", Tone.Success);
        SourceText = "Export da TIA " + DocumentService.When(h.CreatedUtc) + (h.Source == SnapshotSources.Attach ? " (TIA aperto)" : " (copia del progetto)") +
                     " · " + h.DeviceCount + " dispositivi, " + h.ModuleCount + " moduli, " + h.IpCount + " IP, " + h.IoTagCount + " segnali I/O" +
                     (h.Partial ? " · con avvisi" : "");
    }

    private void UpdateFreshness()
    {
        if (_bundle == null)
        {
            return;
        }

        Dictionary<string, DocExport> latest = AppServices.Docs.LatestExports(CommessaId);
        Dictionary<string, DateTime> edits = AppServices.Docs.LastEdits(CommessaId);
        List<IpDevice> ip = AppServices.Commesse.IpDevices(CommessaId);
        List<Change> changes = AppServices.Changes.ByCommessa(CommessaId);
        List<VersionLoad> loads = AppServices.Versions.Loads(CommessaId);
        List<ProjectVersion> versions = _node.Versions.Select(n => n.Version).ToList();
        DateTime? lastPlanChange = AppServices.Plans.Events(CommessaId, null, 1).FirstOrDefault()?.Utc;

        foreach (DocKindVm k in Kinds)
        {
            k.Count = _bundle.Docs.TryGetValue(k.Kind, out DocDocument? doc) ? CountText(doc) : "";
            if (!latest.TryGetValue(k.Kind, out DocExport? last))
            {
                k.LastText = "Mai generato";
                k.Freshness = new ChipVm("Da generare", Tone.Muted);
                k.LastFiles = new List<string>();
                continue;
            }

            string? otherVersion = last.VersionId != null && last.VersionId != _bundle.Version?.Id
                ? versions.FirstOrDefault(v => v.Id == last.VersionId)?.Label
                : null;
            k.LastText = "Generato " + DocumentService.When(last.GeneratedUtc) + " · " + last.Formats.ToUpperInvariant().Replace(",", ", ", StringComparison.Ordinal) +
                         (otherVersion != null ? " · da " + otherVersion : "");
            k.LastFiles = last.Files.Where(File.Exists).ToList();

            List<string> reasons = new();
            DateTime g = last.GeneratedUtc;
            bool fromTia = k.Kind is DocKinds.Hardware or DocKinds.Io || (k.Kind == DocKinds.Ip && !_bundle.IsReference);
            if (fromTia && _bundle.Hardware != null && _bundle.Hardware.Id != last.HwSnapshotId)
            {
                reasons.Add("export da TIA piu' recente");
            }

            if (k.Kind == DocKinds.Ip && _bundle.IsReference && ip.Count > 0 && ip.Max(d => d.UpdatedUtc) > g)
            {
                reasons.Add("Lista IP modificata");
            }

            if (k.Kind == DocKinds.Register &&
                ((changes.Count > 0 && changes.Max(c => c.UpdatedUtc) > g) ||
                 versions.Any(v => v.StateUtc > g) || loads.Any(l => l.CreatedUtc > g || l.EndedUtc > g)))
            {
                reasons.Add("versioni o modifiche cambiate");
            }

            if (k.Kind == DocKinds.Plan && lastPlanChange > g)
            {
                reasons.Add("pianificazione cambiata");
            }

            if ((edits.TryGetValue(k.Kind, out DateTime e) && e > g) || _deletedSince.Contains(k.Kind))
            {
                reasons.Add("note o righe aggiunte cambiate");
            }

            if (otherVersion != null && k.Kind is not (DocKinds.Register or DocKinds.Plan))
            {
                reasons.Add("generato da " + otherVersion);
            }

            if (k.LastFiles.Count == 0)
            {
                reasons.Add("file non piu' presenti");
            }

            k.Freshness = reasons.Count == 0
                ? new ChipVm("Aggiornato", Tone.Success)
                : new ChipVm("Da rigenerare", Tone.Warning, string.Join(", ", reasons));
        }
    }

    private static string CountText(DocDocument doc)
    {
        int n = doc.Primary?.Table?.Rows.Count(r => r.Style != DocRowStyle.Group) ?? 0;
        (string one, string many) = doc.Kind switch
        {
            DocKinds.Ip => ("indirizzo", "indirizzi"),
            DocKinds.Hardware => ("modulo", "moduli"),
            DocKinds.Io => ("segnale", "segnali"),
            DocKinds.Register => ("modifica", "modifiche"),
            DocKinds.Plan => ("attivita'", "attivita'"),
            _ => ("riga", "righe"),
        };
        return n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);
    }

    private void ShowPreview()
    {
        SelectedRow = null;
        DocTable? table = SelectedKind != null && _bundle != null && _bundle.Docs.TryGetValue(SelectedKind.Kind, out DocDocument? d) ? d.Primary?.Table : null;
        PreviewColumns = table?.Columns ?? (IReadOnlyList<DocColumn>)Array.Empty<DocColumn>();
        NoteColumn = table?.NoteColumn ?? -1;
        _allRows = table?.Rows.Select(r => new DocPreviewRow(this, r, PreviewColumns.Count)).ToList() ?? new List<DocPreviewRow>();
        EmptyText = table == null ? null : table.Rows.Count == 0 ? table.EmptyText ?? "Nessun dato." : null;
        PreviewColumnsChanged?.Invoke();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string f = Filter.Trim();
        Rows.Clear();
        foreach (DocPreviewRow r in _allRows)
        {
            if (f.Length == 0 || (!r.IsGroup && r.Matches(f)))
            {
                Rows.Add(r);
            }
        }

        int data = _allRows.Count(r => !r.IsGroup);
        PreviewCount = f.Length == 0 ? CountOf(data) : Rows.Count + " di " + CountOf(data);
    }

    private string CountOf(int n) => n == 1 ? "1 riga" : n + " righe";

    // ---------- modifiche nell'anteprima ----------

    /// <summary>Si puo' modificare la cella? Note su righe di dati, tutto sulle righe manuali.</summary>
    public bool CanEdit(DocPreviewRow row, int column) =>
        CanEditRows && !row.IsGroup && (row.IsManual || column == NoteColumn);

    internal void OnCellEdited(DocPreviewRow row, int column, string? value)
    {
        if (SelectedKind == null || !CanEdit(row, column))
        {
            return;
        }

        string kind = SelectedKind.Kind;
        if (row.IsManual && row.Row.ManualId is long id)
        {
            Dictionary<string, string> cells = new(StringComparer.Ordinal);
            for (int i = 0; i < PreviewColumns.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(row[i]))
                {
                    cells[PreviewColumns[i].Key] = row[i]!.Trim();
                }
            }

            AppServices.Docs.UpdateManualRow(id, cells);
        }
        else if (column == NoteColumn && row.Row.Key.Length > 0)
        {
            AppServices.Docs.SetNote(CommessaId, kind, row.Row.Key, value);
        }

        Status = "Salvato: vale per i prossimi documenti generati.";
        MarkEdited(kind);
    }

    private void MarkEdited(string kind)
    {
        DocKindVm? k = Kinds.FirstOrDefault(x => x.Kind == kind);
        if (k?.Freshness is { Text: "Aggiornato" })
        {
            k.Freshness = new ChipVm("Da rigenerare", Tone.Warning, "note o righe aggiunte cambiate");
        }
    }

    [RelayCommand]
    private async Task AddRow()
    {
        if (!CanEditRows || SelectedKind == null)
        {
            return;
        }

        string firstText = SelectedKind.Kind == DocKinds.Hardware ? "module" : "name";
        long id = AppServices.Docs.AddManualRow(CommessaId, SelectedKind.Kind, new Dictionary<string, string> { [firstText] = "Nuova riga" });
        MarkEdited(SelectedKind.Kind);
        await ReloadAsync(rebuildVersions: false);
        SelectedRow = Rows.FirstOrDefault(r => r.Row.ManualId == id);
        Status = "Riga aggiunta in fondo: doppio clic sulle celle per scriverla.";
    }

    [RelayCommand]
    private async Task RemoveRow()
    {
        if (SelectedKind == null || SelectedRow is not { IsManual: true, Row.ManualId: long id } row)
        {
            Status = "Si possono togliere solo le righe aggiunte a mano.";
            return;
        }

        string kind = SelectedKind.Kind;
        Dictionary<string, string> backup = new(StringComparer.Ordinal);
        for (int i = 0; i < PreviewColumns.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(row[i]))
            {
                backup[PreviewColumns[i].Key] = row[i]!;
            }
        }

        AppServices.Docs.DeleteManualRow(id);
        _deletedSince.Add(kind);
        await ReloadAsync(rebuildVersions: false);
        Notify.Info("Riga tolta", null, new NoticeAction("Annulla", () =>
        {
            AppServices.Docs.AddManualRow(CommessaId, kind, backup);
            _ = ReloadAsync(rebuildVersions: false);
        }));
    }

    // ---------- generazione ----------

    [RelayCommand]
    private Task Export(string? format)
    {
        if (SelectedKind == null)
        {
            return Task.CompletedTask;
        }

        IReadOnlyCollection<DocFormat> formats = format switch
        {
            "docx" => new[] { DocFormat.Docx },
            "pdf" => new[] { DocFormat.Pdf },
            "csv" => new[] { DocFormat.Csv },
            _ => DocExporter.All.ToArray(),
        };
        return Generate(new[] { SelectedKind.Kind }, formats);
    }

    [RelayCommand]
    private Task GenerateAll() => Generate(DocumentService.Kinds.ToArray(), DocExporter.All.ToArray());

    private async Task Generate(IReadOnlyCollection<string> kinds, IReadOnlyCollection<DocFormat> formats)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = "Generazione in corso...";
        try
        {
            DocExportOutcome? r = await DocumentService.ExportAsync(CommessaId, SelectedVersion?.Version.Id, kinds, formats);
            if (r != null)
            {
                foreach (string k in kinds)
                {
                    _deletedSince.Remove(k);
                }

                Status = r.Ok ? "Documenti scritti in " + r.Folder : "Alcuni file non sono stati scritti: vedere la notifica.";
            }
            else
            {
                Status = "";
            }
        }
        catch (Exception ex)
        {
            Log.Error("Generazione documenti", ex);
            Notify.Error("Documenti non generati", ex.Message);
            Status = "";
        }
        finally
        {
            IsBusy = false;
        }

        UpdateFreshness();
        OnPropertyChanged(nameof(ListsFolder));
    }

    [RelayCommand]
    private async Task RefreshFromTia()
    {
        DocVersionChoice? choice = SelectedVersion;
        VersionNode? node = choice == null ? null : _node.Versions.FirstOrDefault(v => v.Version.Id == choice.Version.Id);
        if (node == null)
        {
            Notify.Warning("Aggiorna da TIA", "Scegliere prima una versione.");
            return;
        }

        // La Lista IP si allinea solo per la versione di riferimento; i documenti si rigenerano dopo (se impostato).
        await new VersionActions(_main, node).ReadHardware(forceIpSync: false);
        _dirty = true;
        await ReloadAsync(rebuildVersions: true);
    }

    [RelayCommand]
    private void OpenListsFolder()
    {
        string folder = ListsFolder;
        if (folder.Length == 0)
        {
            Notify.Warning("Elenchi", "La commessa non ha una cartella TiaTrackerOut.");
            return;
        }

        if (_bundle is { IsReference: false, Version: not null } && OutputService.Layout(CommessaId) is OutputLayout layout)
        {
            string sub = layout.ListsDir(_bundle.Version.Label);
            folder = Directory.Exists(sub) ? sub : folder;
        }

        DocumentService.OpenFolder(folder);
    }

    [RelayCommand]
    private void OpenLast(DocKindVm? kind)
    {
        kind ??= SelectedKind;
        string? file = kind?.LastFiles.OrderBy(f => Path.GetExtension(f) == ".pdf" ? 0 : Path.GetExtension(f) == ".docx" ? 1 : 2).FirstOrDefault();
        if (file == null)
        {
            Notify.Info(kind?.Title ?? "Documento", "Non ancora generato (o file spostati): usare i pulsanti DOCX, PDF o CSV.");
            return;
        }

        DocumentService.OpenFile(file);
    }

    // ---------- file della commessa ----------

    private string? DocumentsFolder => OutputService.Layout(CommessaId)?.Documents;

    [RelayCommand]
    private void LoadFiles()
    {
        Files.Clear();
        string? dir = DocumentsFolder;
        if (dir == null || !Directory.Exists(dir))
        {
            FilesText = dir == null ? "Nessuna cartella TiaTrackerOut." : "Cartella Documenti non ancora creata: trascinare qui i file.";
            return;
        }

        try
        {
            foreach (FileInfo f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)
                         .Where(f => !f.Name.StartsWith("~$", StringComparison.Ordinal) && (f.Attributes & FileAttributes.Hidden) == 0)
                         .OrderByDescending(f => f.LastWriteTime).Take(300))
            {
                string rel = Path.GetRelativePath(dir, f.DirectoryName ?? dir);
                Files.Add(new CommessaFileVm(f.FullName, f.Name, rel == "." ? "" : rel, f.LastWriteTime, f.Length));
            }

            FilesText = Files.Count == 0 ? "Nessun file: trascinare qui offerte, schemi, manuali..." : Files.Count + " file in " + dir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FilesText = "Cartella non leggibile: " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenCommessaFile(CommessaFileVm? file)
    {
        file ??= SelectedFile;
        if (file != null)
        {
            DocumentService.OpenFile(file.Path);
        }
    }

    [RelayCommand]
    private void ShowCommessaFile(CommessaFileVm? file)
    {
        file ??= SelectedFile;
        if (file != null)
        {
            DocumentService.ShowInExplorer(file.Path);
        }
    }

    [RelayCommand]
    private void OpenDocumentsFolder()
    {
        string? dir = DocumentsFolder;
        if (dir == null)
        {
            Notify.Warning("Documenti", "La commessa non ha una cartella TiaTrackerOut.");
            return;
        }

        DocumentService.OpenFolder(dir);
    }

    [RelayCommand]
    private void AddFiles()
    {
        OpenFileDialog dialog = new() { Title = "File da copiare nei documenti della commessa", Multiselect = true };
        if (dialog.ShowDialog() == true)
        {
            CopyIn(dialog.FileNames);
        }
    }

    /// <summary>Copia file (o cartelle, trascinati) in Documenti\: mai sovrascrivere, nome con (2), (3)...</summary>
    public void CopyIn(IEnumerable<string> paths)
    {
        string? dir = DocumentsFolder;
        if (dir == null)
        {
            Notify.Warning("Documenti", "La commessa non ha una cartella TiaTrackerOut.");
            return;
        }

        int copied = 0;
        List<string> failed = new();
        foreach (string p in paths)
        {
            try
            {
                if (Directory.Exists(p))
                {
                    string target = Unique(Path.Combine(dir, Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar))));
                    CopyDirectory(p, target);
                    copied++;
                }
                else if (File.Exists(p))
                {
                    Directory.CreateDirectory(dir);
                    if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(p)), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // gia' qui
                    }

                    File.Copy(p, Unique(Path.Combine(dir, Path.GetFileName(p))));
                    copied++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(Path.GetFileName(p) + ": " + ex.Message);
            }
        }

        LoadFiles();
        if (failed.Count > 0)
        {
            Notify.Warning("Alcuni file non copiati", string.Join("\n", failed));
        }
        else if (copied > 0)
        {
            Notify.Success(copied == 1 ? "File copiato nei documenti" : copied + " elementi copiati nei documenti", dir,
                new NoticeAction("Apri cartella", () => DocumentService.OpenFolder(dir)));
        }
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, name + " (" + i + ")" + ext);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string f in Directory.EnumerateFiles(source))
        {
            File.Copy(f, Path.Combine(target, Path.GetFileName(f)));
        }

        foreach (string d in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(d, Path.Combine(target, Path.GetFileName(d)));
        }
    }
}
