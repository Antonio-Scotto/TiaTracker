using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TiaTracker.App.ViewModels;

namespace TiaTracker.App.Views;

public partial class VersionView : UserControl
{
    public VersionView()
    {
        InitializeComponent();
    }

    /// <summary>Il pulsante dello stato apre lo stesso menu del tasto destro sull'albero.</summary>
    private void OnStateMenu(object sender, RoutedEventArgs e)
    {
        if (DataContext is not VersionDetailViewModel vm)
        {
            return;
        }

        ContextMenu menu = VersionMenu.Build(vm.Main, vm.Node);
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
