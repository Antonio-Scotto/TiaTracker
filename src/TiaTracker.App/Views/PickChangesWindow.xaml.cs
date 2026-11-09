using System.Windows;
using System.Windows.Controls;
using TiaTracker.App.ViewModels;
using TiaTracker.Core.Domain;

namespace TiaTracker.App.Views;

public partial class PickChangesWindow : Window
{
    private readonly List<Item> _items;

    /// <param name="versionId">La versione a cui si associano: le modifiche gia' associate lo dicono nell'elenco.</param>
    public PickChangesWindow(IEnumerable<Change> candidates, ChangeState initial = ChangeState.Planned, string? stateLabel = null,
        long? versionId = null)
    {
        InitializeComponent();
        _items = candidates.Select(c => new Item(c, versionId != null && c.Versions.Any(v => v.VersionId == versionId))).ToList();
        List.ItemsSource = _items;
        StateBox.ItemsSource = StateOption.States;
        StateBox.SelectedItem = StateOption.Of(initial);
        if (stateLabel != null)
        {
            StateLabel.Text = stateLabel;
        }
    }

    public List<Change> Selected { get; private set; } = new();

    public ChangeState State => (StateBox.SelectedItem as StateOption)?.State ?? ChangeState.Planned;

    private void OnFilter(object sender, TextChangedEventArgs e)
    {
        string f = FilterBox.Text.Trim();
        List.ItemsSource = f.Length == 0
            ? _items
            : _items.Where(i => i.Title.Contains(f, StringComparison.OrdinalIgnoreCase) || i.Info.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Selected = List.SelectedItems.Cast<Item>().Select(i => i.Change).ToList();
        DialogResult = Selected.Count > 0;
    }

    private sealed class Item
    {
        public Item(Change c, bool onVersion)
        {
            Change = c;
            OnVersion = onVersion;
        }

        public Change Change { get; }

        public bool OnVersion { get; }

        public string Title => Change.Title;

        public string Info => (OnVersion ? "gia' su questa versione · " : "") + ChangeFormat.Date(Change) + " · " + ChangeFormat.Blocks(Change);
    }
}
