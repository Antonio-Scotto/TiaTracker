using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TiaTracker.App.ViewModels;

namespace TiaTracker.App.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Activate();
    }

    private DashboardViewModel? Vm => DataContext as DashboardViewModel;

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => Activate();

    private void Activate()
    {
        if (Vm == null)
        {
            return;
        }

        Vm.IsShown = IsVisible;
        if (IsVisible)
        {
            Vm.EnsureLoaded();
        }

        NoLoads.Visibility = Vm.Loads.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Vm.Loads.CollectionChanged -= OnLoadsChanged;
        Vm.Loads.CollectionChanged += OnLoadsChanged;
    }

    private void OnLoadsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        NoLoads.Visibility = Vm?.Loads.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Lo stesso menu del tasto destro nell'albero: stati e azioni della versione.</summary>
    private void OnVersionRightClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm != null && sender is FrameworkElement { DataContext: VersionNode node } fe)
        {
            ContextMenu menu = VersionMenu.Build(Vm.Main, node);
            menu.PlacementTarget = fe;
            menu.Closed += (_, _) => Vm.Invalidate();
            menu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void OnVersionClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: VersionNode node })
        {
            Vm?.OpenVersionCommand.Execute(node);
        }
    }
}
