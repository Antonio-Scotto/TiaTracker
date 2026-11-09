using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using TiaTracker.Core.Domain;

namespace TiaTracker.App.Views;

/// <summary>Un PLC nel dialogo, con la versione che c'e' sopra adesso.</summary>
public sealed partial class PlcChoice : ObservableObject
{
    public PlcChoice(string name, string now)
    {
        Name = name;
        Now = now;
    }

    public string Name { get; }

    public string Now { get; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

public partial class LoadedWindow : Window
{
    private readonly List<PlcChoice> _plcs;

    /// <param name="knownPlcs">PLC gia' visti nella commessa (carichi, snapshot).</param>
    /// <param name="currentLoads">Carichi correnti della commessa, per dire cosa c'e' adesso su ogni PLC.</param>
    /// <param name="labels">Etichette delle versioni della commessa.</param>
    public LoadedWindow(ProjectVersion version, IReadOnlyList<string> knownPlcs, IReadOnlyList<VersionLoad> currentLoads,
        IReadOnlyDictionary<long, string> labels, bool openInTia)
    {
        InitializeComponent();
        Header.Text = version.Label + " caricata sul PLC";
        Title = version.Label + " caricata sul PLC";

        HashSet<string> mine = currentLoads.Where(l => l.VersionId == version.Id).Select(l => l.Plc).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _plcs = knownPlcs.Select(p =>
        {
            VersionLoad? on = currentLoads.FirstOrDefault(l => l.Plc.Equals(p, StringComparison.OrdinalIgnoreCase));
            string now = on == null ? "  libero"
                : on.VersionId == version.Id ? "  gia' questa versione"
                : "  ora: " + (labels.TryGetValue(on.VersionId, out string? l) ? l : "?") + " (diventera' Vecchia)";
            return new PlcChoice(p, now) { IsChecked = mine.Contains(p) };
        }).ToList();

        // Un solo PLC conosciuto: e' quasi sempre quello.
        if (_plcs.Count == 1)
        {
            _plcs[0].IsChecked = true;
        }

        PlcList.ItemsSource = _plcs;
        if (_plcs.Count == 0)
        {
            OtherPlc.Text = "";
        }

        SetNow();
        ProofBox.IsEnabled = openInTia;
        ProofBox.IsChecked = openInTia;
        ProofHint.Text = openInTia
            ? "La versione e' aperta in TIA: lo snapshot e' la prova di cosa e' stato caricato (dura pochi minuti, non lavorare in TIA nel frattempo)."
            : "La versione non e' aperta in TIA: dopo la conferma si potra' fare lo snapshot dalla cartella.";
    }

    public DateTime LoadedUtc { get; private set; }

    /// <summary>I PLC scelti; "" = PLC senza nome (nessuno indicato).</summary>
    public List<string> Plcs { get; private set; } = new();

    public string? Note => string.IsNullOrWhiteSpace(NoteBox.Text) ? null : NoteBox.Text.Trim();

    public bool TakeProofSnapshot => ProofBox.IsChecked == true && ProofBox.IsEnabled;

    private void SetNow()
    {
        DateTime local = DateTime.Now;
        DateBox.SelectedDate = local.Date;
        TimeBox.Text = local.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private void OnNow(object sender, RoutedEventArgs e) => SetNow();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (DateBox.SelectedDate == null)
        {
            MessageBox.Show(this, "Indicare la data.", Title);
            return;
        }

        TimeSpan time = TimeSpan.Zero;
        if (!string.IsNullOrWhiteSpace(TimeBox.Text) &&
            !TimeSpan.TryParseExact(TimeBox.Text.Trim(), new[] { @"h\:mm", @"hh\:mm", @"h\.mm", @"hh\.mm" }, CultureInfo.InvariantCulture, out time))
        {
            MessageBox.Show(this, "Ora non valida (hh:mm).", Title);
            return;
        }

        List<string> plcs = _plcs.Where(p => p.IsChecked).Select(p => p.Name).ToList();
        foreach (string other in OtherPlc.Text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            plcs.Add(other);
        }

        if (plcs.Count == 0 && _plcs.Count > 0)
        {
            MessageBox.Show(this, "Scegliere almeno un PLC (o scriverne il nome in \"Altro PLC\").", Title);
            return;
        }

        Plcs = plcs.Count == 0 ? new List<string> { "" } : plcs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        LoadedUtc = DateTime.SpecifyKind(DateBox.SelectedDate.Value.Date + time, DateTimeKind.Local).ToUniversalTime();
        DialogResult = true;
    }
}
