using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TiaTracker.App.Services;
using TiaTracker.Core.Domain;

namespace TiaTracker.App.ViewModels;

/// <summary>
/// Matrice modifiche x versioni: a colpo d'occhio la stessa patch su V0.68
/// e V0.70 con stati diversi. Ogni cella si cambia da tendina.
/// </summary>
public sealed partial class MatrixViewModel : ObservableObject
{
    private readonly CommessaNode _node;

    public MatrixViewModel(CommessaNode node)
    {
        _node = node;
    }

    public List<ProjectVersion> Columns { get; private set; } = new();

    public ObservableCollection<MatrixRow> Rows { get; } = new();

    public IReadOnlyList<StateOption> Options => StateOption.WithNone;

    [ObservableProperty]
    public partial bool HideOld { get; set; }

    partial void OnHideOldChanged(bool value) => Reload();

    /// <summary>La vista ricostruisce le colonne quando cambiano le versioni.</summary>
    public event Action? ColumnsChanged;

    public void Reload()
    {
        List<ProjectVersion> versions = _node.Versions
            .Where(v => !v.Version.Missing)
            .Where(v => !HideOld || !v.Status.IsRetired)
            .Select(v => v.Version)
            .ToList();
        Columns = versions;

        Rows.Clear();
        foreach (Change c in AppServices.Changes.ByCommessa(_node.Commessa.Id))
        {
            Rows.Add(new MatrixRow(c, versions));
        }

        ColumnsChanged?.Invoke();
    }
}

public sealed class MatrixRow
{
    public MatrixRow(Change change, IReadOnlyList<ProjectVersion> columns)
    {
        Change = change;
        Cells = columns.Select(v => new MatrixCell(change, v, change.Versions.FirstOrDefault(x => x.VersionId == v.Id))).ToList();
    }

    public Change Change { get; }

    public string Title => (Change.Draft ? "[bozza] " : "") + Change.Title;

    public string DateText => ChangeFormat.Date(Change);

    public string Blocks => ChangeFormat.Blocks(Change);

    public List<MatrixCell> Cells { get; }
}

public sealed partial class MatrixCell : ObservableObject
{
    private readonly Change _change;
    private readonly ProjectVersion _version;
    private bool _loading;

    public MatrixCell(Change change, ProjectVersion version, ChangeVersion? cv)
    {
        _change = change;
        _version = version;
        _loading = true;
        Option = StateOption.Of(cv?.State);
        _loading = false;
        Tooltip = cv == null
            ? version.Label + ": non prevista"
            : version.Label + ": " + ChangeFormat.StateText(cv) +
              (cv.StateUtc.HasValue ? " dal " + cv.StateUtc.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm") : "") +
              (string.IsNullOrWhiteSpace(cv.Note) ? "" : "\n" + cv.Note);
    }

    [ObservableProperty]
    public partial StateOption Option { get; set; }

    [ObservableProperty]
    public partial string Tooltip { get; set; }

    /// <summary>Compilata o importata senza salvataggio: la cella va evidenziata.</summary>
    public bool NotSaved => Option.State is ChangeState.Imported or ChangeState.Compiled;

    partial void OnOptionChanged(StateOption value)
    {
        OnPropertyChanged(nameof(NotSaved));
        if (_loading)
        {
            return;
        }

        if (value.State == null)
        {
            AppServices.Changes.RemoveFromVersion(_change.Id, _version.Id);
            Tooltip = _version.Label + ": non prevista";
        }
        else
        {
            AppServices.Changes.SetVersionState(_change.Id, _version.Id, value.State.Value);
            Tooltip = _version.Label + ": " + value.Text + " dal " + DateTime.Now.ToString("dd/MM/yyyy HH:mm");
        }
    }
}
