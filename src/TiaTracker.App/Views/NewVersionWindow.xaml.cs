using System.Windows;
using System.Windows.Controls;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Output;

namespace TiaTracker.App.Views;

public partial class NewVersionWindow : Window
{
    /// <param name="sourceToOld">Proposta per "Segna la partenza Vecchia" (si' se era In lavoro o senza stato e non e' su un PLC).</param>
    public NewVersionWindow(ProjectVersion source, string proposedName, bool sourceOpen, bool sourceToOld)
    {
        InitializeComponent();
        SourceOldBox.Content = "Segna " + source.Label + " come Vecchia";
        SourceOldBox.IsChecked = sourceToOld;
        Header.Text = "Copia della cartella di " + source.Label + " (" + source.ProjectName + ") accanto all'originale, " +
                      "senza lock, TMP e Logs. Il file progetto viene rinominato come la cartella.";
        NameBox.Text = proposedName;
        NameBox.SelectAll();
        OpenWarning.Visibility = sourceOpen ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => NameBox.Focus();
    }

    public string NewName => NameBox.Text.Trim();

    public bool CarryChanges => CarryBox.IsChecked == true;

    public bool MarkActive => ActiveBox.IsChecked == true;

    public bool SourceToOld => SourceOldBox.IsChecked == true;

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        Error.Text = VersionNaming.IsValidName(NameBox.Text.Trim()) ? "" : "Nome non valido per una cartella di Windows.";
        OkButton.IsEnabled = Error.Text.Length == 0;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!VersionNaming.IsValidName(NewName))
        {
            return;
        }

        DialogResult = true;
    }
}
