using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using TiaTracker.App.Services;
using TiaTracker.Core.Domain;

namespace TiaTracker.App.ViewModels;

/// <summary>Voce delle tendine di stato. State null = modifica non associata alla versione.</summary>
public sealed record StateOption(ChangeState? State, string Text)
{
    public static readonly StateOption None = new(null, "—");

    public static readonly IReadOnlyList<StateOption> WithNone = new[]
    {
        None,
        new StateOption(ChangeState.Planned, "Pianificata"),
        new StateOption(ChangeState.Imported, "Importata"),
        new StateOption(ChangeState.Compiled, "Compilata"),
        new StateOption(ChangeState.Saved, "Salvata"),
        new StateOption(ChangeState.Dropped, "Scartata"),
    };

    public static readonly IReadOnlyList<StateOption> States = WithNone.Skip(1).ToArray();

    public static StateOption Of(ChangeState? s) => WithNone.First(o => o.State == s);

    public override string ToString() => Text;
}

public static class ChangeFormat
{
    public static string Date(Change c) =>
        c.Date?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ??
        c.CreatedUtc.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    public static string Blocks(Change c) => string.Join(", ", c.Blocks.Select(b => b.BlockName));

    public static string StateText(ChangeVersion cv)
    {
        string text = ChangeStates.Italian(cv.State);
        if (cv.State is ChangeState.Imported or ChangeState.Compiled)
        {
            text += " (non salvata)";
        }

        if (cv.Errors.HasValue || cv.Warnings.HasValue)
        {
            text += $" · {cv.Errors ?? 0} err / {cv.Warnings ?? 0} warn";
        }

        return text;
    }
}

/// <summary>Riga della scheda Modifiche di una versione: la modifica e il suo stato su quella versione.</summary>
public sealed partial class VersionChangeRow : ObservableObject
{
    private readonly long _versionId;
    private bool _loading;

    public VersionChangeRow(Change change, ChangeVersion cv)
    {
        Change = change;
        _versionId = cv.VersionId;
        _loading = true;
        Option = StateOption.Of(cv.State);
        _loading = false;
        Cv = cv;
    }

    public Change Change { get; }

    public ChangeVersion Cv { get; private set; }

    public string Title => Change.Title + (Change.Draft ? "  [bozza]" : "");

    public string DateText => ChangeFormat.Date(Change);

    public string Scope => ChangeStates.ScopeItalian(Change.Scope);

    public string Blocks => ChangeFormat.Blocks(Change);

    public string StateText => ChangeFormat.StateText(Cv);

    public string? Note => Cv.Note;

    public bool NotSaved => Cv.State is ChangeState.Imported or ChangeState.Compiled;

    [ObservableProperty]
    public partial StateOption Option { get; set; }

    partial void OnOptionChanged(StateOption value)
    {
        if (_loading || value.State == null || value.State == Cv.State)
        {
            return;
        }

        AppServices.Changes.SetVersionState(Change.Id, _versionId, value.State.Value);
        Cv.State = value.State.Value;
        Cv.StateUtc = DateTime.UtcNow;
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(NotSaved));
        StateChanged?.Invoke();
    }

    public Action? StateChanged { get; set; }
}

/// <summary>Riga dell'elenco modifiche di commessa.</summary>
public sealed class CommessaChangeRow
{
    public CommessaChangeRow(Change change, IReadOnlyDictionary<long, ProjectVersion> versions)
    {
        Change = change;
        VersionsText = string.Join("   ", change.Versions
            .Where(v => versions.ContainsKey(v.VersionId))
            .OrderByDescending(v => versions[v.VersionId].SortKey, StringComparer.Ordinal)
            .Select(v => versions[v.VersionId].Label + ": " + ChangeStates.Italian(v.State) +
                         (v.State is ChangeState.Imported or ChangeState.Compiled ? " (non salvata)" : "")));
        NotSaved = change.Versions.Any(v => v.State is ChangeState.Imported or ChangeState.Compiled);
    }

    public Change Change { get; }

    public string Title => Change.Title;

    public bool Draft => Change.Draft;

    public string DateText => ChangeFormat.Date(Change);

    public string Scope => ChangeStates.ScopeItalian(Change.Scope);

    public string Blocks => ChangeFormat.Blocks(Change);

    public string VersionsText { get; }

    public bool NotSaved { get; }

    public string? Confidence => Change.Confidence;
}
