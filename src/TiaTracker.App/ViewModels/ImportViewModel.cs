using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TiaTracker.App.Services;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Import;

namespace TiaTracker.App.ViewModels;

public sealed partial class ProposalRow : ObservableObject
{
    public ProposalRow(HistoryProposal p, ScanRoot root)
    {
        Proposal = p;
        Root = root;

        // Le bozze a bassa confidenza non sono spuntate; gia' importate nemmeno.
        Selected = !p.AlreadyImported && p.Draft.Confidence != "bassa";
    }

    public HistoryProposal Proposal { get; }

    public ScanRoot Root { get; }

    [ObservableProperty]
    public partial bool Selected { get; set; }

    public DraftChange Draft => Proposal.Draft;

    public string Title => Draft.Title;

    public string Date => Draft.Date?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "";

    public string Confidence => Draft.Confidence + (Proposal.AlreadyImported ? " · gia' importata" : "");

    public string Blocks => string.Join(", ", Draft.Blocks.Select(b => b.Name + (b.Action != null ? " (" + b.Action + ")" : "")));

    public string Versions => string.Join("   ", Draft.Versions.Select(v =>
        v.Label + ": " + ChangeStates.Italian(v.State) + (v.NotSaved ? " non salvata" : "") +
        (v.Errors.HasValue || v.Warnings.HasValue ? $" ({v.Errors ?? 0} err, {v.Warnings ?? 0} warn)" : "")));

    public string Sources => string.Join("\n", Proposal.Sources.Select(s => s.RelPath));

    public string Detail =>
        "Fonti:\n" + Sources + "\n\nData da: " + Draft.DateSource + "\nAmbito: " + ChangeStates.ScopeItalian(Draft.Scope) +
        "\n\nStati per versione:\n" + string.Join("\n", Draft.Versions.Select(v => "  " + v.Label + " = " + ChangeStates.Italian(v.State) +
                                                                                   (v.NotSaved ? " (non salvata)" : "") + (v.Line != null ? "\n      «" + v.Line + "»" : ""))) +
        "\n\nDescrizione:\n" + Draft.Description + "\n\nEstratto:\n" + Draft.Excerpt;
}

/// <summary>
/// Importazione assistita dello storico: proposte dai documenti, la spunta
/// dell'utente le crea come bozze. Nulla entra definitivo da solo.
/// </summary>
public sealed partial class ImportViewModel : ObservableObject
{
    private readonly CommessaNode _node;
    private readonly MainViewModel _main;

    public ImportViewModel(CommessaNode node, MainViewModel main)
    {
        _node = node;
        _main = main;
        Globs = string.Join("\n", HistoryImporter.Globs(node.Roots.FirstOrDefault()?.HistoryGlobs));
        Scan();
    }

    public string Title => "Importa storico - " + _node.Commessa.Display;

    public ObservableCollection<ProposalRow> Rows { get; } = new();

    [ObservableProperty]
    public partial ProposalRow? Selected { get; set; }

    [ObservableProperty]
    public partial string Globs { get; set; }

    [ObservableProperty]
    public partial bool ShowImported { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    public Action? Close { get; set; }

    partial void OnShowImportedChanged(bool value) => Scan();

    [RelayCommand]
    private void Scan()
    {
        Rows.Clear();
        List<string> labels = AppServices.Versions.ByCommessa(_node.Commessa.Id).Select(v => v.Label).ToList();
        IReadOnlyList<string> globs = HistoryImporter.Globs(Globs);
        int files = 0;
        foreach (ScanRoot root in _node.Roots.Where(r => Directory.Exists(r.Path)))
        {
            List<string> found = HistoryImporter.FindFiles(root.Path, globs);
            files += found.Count;
            foreach (HistoryProposal p in HistoryImporter.Propose(root.Path, found, labels, sha => AppServices.Imports.IsImported(_node.Commessa.Id, sha)))
            {
                if (p.AlreadyImported && !ShowImported)
                {
                    continue;
                }

                Rows.Add(new ProposalRow(p, root));
            }
        }

        Selected = Rows.FirstOrDefault();
        Summary = $"{files} documenti, {Rows.Count} proposte, {Rows.Count(r => r.Selected)} spuntate. " +
                  "Le proposte spuntate diventano BOZZE: si confermano aprendole e togliendo \"Bozza\".";
    }

    [RelayCommand]
    private void SaveGlobs()
    {
        foreach (ScanRoot r in _node.Roots)
        {
            r.HistoryGlobs = string.Join("\n", HistoryImporter.DefaultGlobs) == Globs.Replace("\r", "", StringComparison.Ordinal).Trim() ? null : Globs;
            AppServices.Commesse.UpdateScanRoot(r);
        }

        Scan();
    }

    [RelayCommand]
    private void Import()
    {
        List<ProposalRow> chosen = Rows.Where(r => r.Selected).ToList();
        if (chosen.Count == 0)
        {
            return;
        }

        Dictionary<string, ProjectVersion> byLabel = AppServices.Versions.ByCommessa(_node.Commessa.Id)
            .GroupBy(v => v.Label, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        int n = 0;
        foreach (ProposalRow row in chosen)
        {
            DraftChange d = row.Draft;
            long? sourceId = null;
            foreach ((string rel, string sha) in row.Proposal.Sources)
            {
                long id = AppServices.Imports.AddSource(_node.Commessa.Id, rel, sha, d.Confidence);
                sourceId ??= id;
            }

            Change c = new()
            {
                CommessaId = _node.Commessa.Id,
                Title = d.Title,
                Description = d.Description,
                Date = d.Date,
                Scope = d.Scope,
                Draft = true,
                Confidence = d.Confidence,
                Excerpt = "Da: " + string.Join(" + ", row.Proposal.Sources.Select(s => s.RelPath)) + "\n\n" + d.Excerpt,
                ImportSourceId = sourceId,
                Blocks = d.Blocks.Select(b => new ChangeBlock { BlockName = b.Name, Action = b.Action }).ToList(),
            };

            foreach (VersionEvidence v in d.Versions)
            {
                if (byLabel.TryGetValue(v.Label, out ProjectVersion? version))
                {
                    c.Versions.Add(new ChangeVersion
                    {
                        VersionId = version.Id,
                        State = v.State,
                        Errors = v.Errors,
                        Warnings = v.Warnings,
                        StateUtc = d.Date?.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Local).ToUniversalTime(),
                        Note = v.NotSaved ? "progetto NON salvato (dal documento)" : null,
                    });
                }
            }

            AppServices.Changes.Save(c, "importata da " + row.Proposal.Sources[0].RelPath);
            n++;
        }

        Log.Info($"Storico: {n} bozze importate per {_node.Commessa.Code}");
        Notify.Success("Importa storico", n + " bozze create: sono nella scheda Modifiche della commessa, segnate \"bozza\".");
        Close?.Invoke();
    }

    [RelayCommand]
    private void SelectAll() => SetAll(true);

    [RelayCommand]
    private void SelectNone() => SetAll(false);

    private void SetAll(bool value)
    {
        foreach (ProposalRow r in Rows)
        {
            r.Selected = value;
        }
    }
}
