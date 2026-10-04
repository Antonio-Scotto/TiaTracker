using System.Globalization;
using System.Windows;
using System.Windows.Input;
using TiaTracker.Contracts;
using TiaTracker.Core.Domain;

namespace TiaTracker.App.Views;

/// <summary>
/// Nessuna istanza di TIA ha aperto proprio il progetto della versione: si
/// sceglie a mano da quale fare lo snapshot (prima con piu' istanze aperte si
/// falliva e basta).
/// </summary>
public partial class InstancePickerWindow : Window
{
    public sealed record Row(InstanceInfo Instance, string Project, string Info);

    public InstancePickerWindow()
    {
        InitializeComponent();
    }

    /// <summary>Null se l'utente annulla.</summary>
    public static InstanceInfo? Pick(ProjectVersion version, string? projectFile, IReadOnlyList<InstanceInfo> instances)
    {
        InstancePickerWindow w = new() { Owner = Application.Current.MainWindow };
        w.Message.Text = "Nessuna istanza di TIA Portal ha aperto il progetto di " + version.Label +
                         (projectFile != null ? " (" + projectFile + ")" : "") + ". Istanze aperte:";
        w.Warning.Text = "Lo snapshot verra' registrato come " + version.Label +
                         ": scegliere un'altra istanza solo se contiene davvero questa versione (per esempio una copia del progetto).";
        w.List.ItemsSource = instances.Select(i => new Row(i, i.ProjectPath ?? "(nessun progetto)",
            "PID " + i.Pid.ToString(CultureInfo.InvariantCulture) + " · " + i.Mode + Started(i.AcquisitionTime))).ToList();
        if (instances.Count == 1)
        {
            w.List.SelectedIndex = 0;
        }

        return w.ShowDialog() == true && w.List.SelectedItem is Row r ? r.Instance : null;
    }

    private static string Started(string? iso) =>
        DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime t)
            ? " · aperta dalle " + t.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
            : "";

    private void OnSelection(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        OkButton.IsEnabled = List.SelectedItem != null;

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem != null)
        {
            DialogResult = true;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
