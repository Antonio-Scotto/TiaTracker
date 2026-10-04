using System.Windows;
using System.Windows.Controls;
using TiaTracker.App.ViewModels;

namespace TiaTracker.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;

        // Tornando all'app dopo un salvataggio in TIA le versioni si aggiornano da sole.
        Activated += (_, _) => (DataContext as MainViewModel)?.OnActivated();
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Ctrl+F: al filtro dell'albero da qualunque punto della finestra.
        if (e.Key == System.Windows.Input.Key.F && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            FilterBox.Focus();
            FilterBox.SelectAll();
            e.Handled = true;
        }
    }

    private bool _rightClickOnItem;

    /// <summary>Il tasto destro seleziona la riga sotto il mouse prima di aprire il menu.</summary>
    private void OnTreeRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        TreeViewItem? item = FindItem(e.OriginalSource as DependencyObject);
        _rightClickOnItem = item != null;
        if (item != null)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void OnTreeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Da tastiera (Maiusc+F10, tasto menu) CursorLeft e' -1: vale la riga selezionata.
        bool fromKeyboard = e.CursorLeft < 0;
        if (DataContext is not MainViewModel vm || (!fromKeyboard && !_rightClickOnItem))
        {
            e.Handled = true;
            return;
        }

        _rightClickOnItem = false;
        ContextMenu? menu = vm.SelectedNode switch
        {
            VersionNode v => VersionMenu.Build(vm, v),
            CommessaNode c => VersionMenu.BuildCommessa(vm, c),
            _ => null,
        };

        // Mai null: senza un ContextMenu WPF smetterebbe di chiamare questo evento.
        if (menu == null)
        {
            e.Handled = true;
            return;
        }

        Tree.ContextMenu = menu;
    }

    private static TreeViewItem? FindItem(DependencyObject? d)
    {
        while (d != null && d is not TreeViewItem)
        {
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }

        return d as TreeViewItem;
    }

    // TreeView.SelectedItem e' in sola lettura: la selezione passa di qui.
    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.SelectedNode = e.NewValue;
        }
    }
}
