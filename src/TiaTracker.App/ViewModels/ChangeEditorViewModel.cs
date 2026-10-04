using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TiaTracker.Core.Domain;

namespace TiaTracker.App.ViewModels;

public sealed partial class BlockRow : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string? Action { get; set; }

    [ObservableProperty]
    public partial string? Note { get; set; }
}

public sealed partial class VersionStateRow : ObservableObject
{
    public VersionStateRow(ProjectVersion version, ChangeVersion? cv)
    {
        Version = version;

        // Prima lo stato e poi Included: scegliere uno stato spunta la versione.
        Option = StateOption.Of(cv?.State ?? ChangeState.Planned);
        Included = cv != null;
        Errors = cv?.Errors;
        Warnings = cv?.Warnings;
        Note = cv?.Note;
        Original = cv;
    }

    public ProjectVersion Version { get; }

    public ChangeVersion? Original { get; }

    public string Label => Version.Label + (Version.Missing ? " (mancante)" : "");

    [ObservableProperty]
    public partial bool Included { get; set; }

    [ObservableProperty]
    public partial StateOption Option { get; set; }

    [ObservableProperty]
    public partial int? Errors { get; set; }

    [ObservableProperty]
    public partial int? Warnings { get; set; }

    [ObservableProperty]
    public partial string? Note { get; set; }

    partial void OnOptionChanged(StateOption value)
    {
        if (value.State != null)
        {
            Included = true;
        }
    }
}

/// <summary>
/// Editor di una modifica: testo, blocchi toccati e stato per ogni versione.
/// Aperto da una versione (<paramref name="fromVersion"/>) testo e blocchi sono in
/// sola lettura: si creano e si cambiano dalla commessa; restano modificabili le
/// versioni associate e il loro stato.
/// </summary>
public sealed partial class ChangeEditorViewModel : ObservableObject
{
    private readonly Change _original;

    public ChangeEditorViewModel(Change change, IEnumerable<ProjectVersion> versions, IReadOnlyList<string> knownNames, ProjectVersion? fromVersion = null)
    {
        _original = change;
        FromVersion = fromVersion;
        Title = change.Title;
        Description = change.Description;
        Motivation = change.Motivation;
        Date = change.Date?.ToDateTime(TimeOnly.MinValue) ?? (change.Id == 0 ? DateTime.Today : null);
        ScopeIndex = (int)change.Scope;
        Draft = change.Draft;
        Excerpt = change.Excerpt;
        KnownNames = knownNames;
        foreach (ChangeBlock b in change.Blocks)
        {
            Blocks.Add(new BlockRow { Name = b.BlockName, Action = b.Action, Note = b.Note });
        }

        foreach (ProjectVersion v in versions.OrderByDescending(v => v.SortKey, StringComparer.Ordinal))
        {
            ChangeVersion? cv = change.Versions.FirstOrDefault(x => x.VersionId == v.Id);
            if (v.Missing && cv == null)
            {
                continue;
            }

            VersionRows.Add(new VersionStateRow(v, cv));
        }
    }

    public string WindowTitle => _original.Id == 0 ? "Nuova modifica" : "Modifica: " + _original.Title;

    public ProjectVersion? FromVersion { get; }

    /// <summary>Titolo, testi e blocchi non si cambiano (aperta da una versione).</summary>
    public bool ContentReadOnly => FromVersion != null;

    public bool CanEditContent => !ContentReadOnly;

    public string ReadOnlyHint => FromVersion == null ? ""
        : "Aperta dalla versione " + FromVersion.Label + ": titolo, testi e blocchi si cambiano nella scheda Modifiche della commessa. " +
          "Qui si sceglie su quali versioni va la modifica e a che punto e' su ciascuna.";

    public IReadOnlyList<string> KnownNames { get; }

    public IReadOnlyList<StateOption> StateOptions => StateOption.States;

    public IReadOnlyList<string> Scopes { get; } = new[] { "PLC", "Ignition", "Altro" };

    public IReadOnlyList<string> Actions { get; } = new[] { "", "nuovo", "sovrascrive", "modifica", "elimina" };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string? Description { get; set; }

    [ObservableProperty]
    public partial string? Motivation { get; set; }

    [ObservableProperty]
    public partial DateTime? Date { get; set; }

    [ObservableProperty]
    public partial int ScopeIndex { get; set; }

    [ObservableProperty]
    public partial bool Draft { get; set; }

    public string? Excerpt { get; }

    public bool HasExcerpt => !string.IsNullOrWhiteSpace(Excerpt);

    public ObservableCollection<BlockRow> Blocks { get; } = new();

    public ObservableCollection<VersionStateRow> VersionRows { get; } = new();

    [ObservableProperty]
    public partial string NewBlockText { get; set; } = "";

    public Action<bool>? Close { get; set; }

    /// <summary>
    /// Accetta uno o piu' nomi separati da a capo, virgole, punti e virgola o
    /// spazi: si incolla direttamente l'elenco "Blocchi da toccare" di un README.
    /// </summary>
    [RelayCommand]
    private void AddBlocks()
    {
        foreach (string raw in NewBlockText.Split(new[] { '\n', '\r', ',', ';', ' ', '\t', '`' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string name = raw.Trim().Trim('"', '\'', '*');
            if (name.Length == 0 || Blocks.Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Blocks.Add(new BlockRow { Name = name });
        }

        NewBlockText = "";
    }

    [RelayCommand]
    private void RemoveBlock(BlockRow? row)
    {
        if (row != null)
        {
            Blocks.Remove(row);
        }
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(Title);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (CanEditContent && !string.IsNullOrWhiteSpace(NewBlockText))
        {
            AddBlocks();
        }

        Close?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => Close?.Invoke(false);

    public Change ToChange()
    {
        Change c = new()
        {
            Id = _original.Id,
            CommessaId = _original.CommessaId,
            Title = Title.Trim(),
            Description = Description,
            Motivation = Motivation,
            Date = Date.HasValue ? DateOnly.FromDateTime(Date.Value) : null,
            Scope = (ChangeScope)ScopeIndex,
            Draft = Draft,
            Confidence = _original.Confidence,
            Excerpt = _original.Excerpt,
            ImportSourceId = _original.ImportSourceId,
            CreatedUtc = _original.CreatedUtc,
        };

        c.Blocks = Blocks.Where(b => !string.IsNullOrWhiteSpace(b.Name)).Select(b => new ChangeBlock
        {
            BlockName = b.Name.Trim(),
            Action = string.IsNullOrWhiteSpace(b.Action) ? null : b.Action,
            Note = b.Note,
            Family = _original.Blocks.FirstOrDefault(o => o.BlockName.Equals(b.Name.Trim(), StringComparison.OrdinalIgnoreCase))?.Family,
        }).ToList();

        foreach (VersionStateRow row in VersionRows.Where(r => r.Included && r.Option.State != null))
        {
            ChangeVersion cv = new()
            {
                VersionId = row.Version.Id,
                State = row.Option.State!.Value,
                Errors = row.Errors,
                Warnings = row.Warnings,
                Note = row.Note,
                EvidenceRunId = row.Original?.EvidenceRunId,
            };

            // La data dello stato cambia solo se cambia lo stato.
            if (row.Original != null && row.Original.State == cv.State)
            {
                cv.StateUtc = row.Original.StateUtc;
            }

            c.Versions.Add(cv);
        }

        return c;
    }
}
