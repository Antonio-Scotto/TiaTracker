using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TiaTracker.App.Controls;
using TiaTracker.App.ViewModels;

namespace TiaTracker.App.Views;

public partial class PlanningView : UserControl
{
    private PlanningViewModel? _vm;
    private ScrollViewer? _gridScroll;

    public PlanningView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null)
            {
                _vm.IsShown = false;
            }

            _vm = DataContext as PlanningViewModel;
            if (_vm != null && IsVisible)
            {
                _vm.IsShown = true;
                _vm.EnsureLoaded();
            }
        };

        Loaded += (_, _) => HookScroll();
        Chart.ViewChanged += (_, _) => SyncScrollBar();
        Chart.SizeChanged += (_, _) => SyncScrollBar();
        Chart.VerticalScrollRequested += (_, delta) => _gridScroll?.ScrollToVerticalOffset(_gridScroll.VerticalOffset + delta);
        Chart.RowClicked += (_, id) =>
        {
            if (_vm != null && id != null)
            {
                _vm.Selected = _vm.Rows.FirstOrDefault(r => r.Id == id);
                Grid.ScrollIntoView(_vm.Selected);
            }
        };
        Chart.BarDoubleClick += (_, id) =>
        {
            if (_vm != null)
            {
                _vm.Selected = _vm.Rows.FirstOrDefault(r => r.Id == id);
                _vm.EditCommand.Execute(_vm.Selected);
            }
        };
        Chart.BarMoved += (_, e) => _vm?.OnBarMoved(e.Id, e.Start, e.End);
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm == null)
        {
            return;
        }

        _vm.IsShown = IsVisible;
        if (IsVisible)
        {
            _vm.EnsureLoaded();
            HookScroll();
        }
    }

    /// <summary>La griglia comanda lo scorrimento verticale del Gantt (stesse righe da 32 px).</summary>
    private void HookScroll()
    {
        if (_gridScroll != null)
        {
            return;
        }

        _gridScroll = FindChild<ScrollViewer>(Grid);
        if (_gridScroll != null)
        {
            _gridScroll.ScrollChanged += (_, e) => Chart.VerticalOffset = e.VerticalOffset;
        }
    }

    private void SyncScrollBar()
    {
        double viewport = Chart.ActualWidth;
        HScroll.Minimum = 0;
        HScroll.Maximum = Math.Max(0, Chart.ContentWidth - viewport);
        HScroll.ViewportSize = viewport;
        HScroll.LargeChange = Math.Max(1, viewport * 0.8);
        HScroll.SmallChange = Chart.DayWidth * 7;
        HScroll.Value = Chart.HorizontalOffset;
    }

    private void OnHScroll(object sender, ScrollEventArgs e) => Chart.HorizontalOffset = HScroll.Value;

    private void OnZoomIn(object sender, RoutedEventArgs e) => Chart.Zoom(1.4);

    private void OnZoomOut(object sender, RoutedEventArgs e) => Chart.Zoom(1 / 1.4);

    private void OnToday(object sender, RoutedEventArgs e) => Chart.ScrollToDate(GanttChart.Today);

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<DataGridRow>(d) is { Item: PlanRowVm row })
        {
            _vm?.EditCommand.Execute(row);
        }
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        ContextMenu menu = new() { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        foreach ((string text, string format) in new[] { ("DOCX, PDF e CSV", "all"), ("Solo PDF", "pdf"), ("Solo DOCX", "docx"), ("Solo CSV", "csv") })
        {
            MenuItem item = new() { Header = text };
            item.Click += (_, _) => _vm?.ExportCommand.Execute(format);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null || e.OriginalSource is TextBox)
        {
            return;
        }

        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _vm.UndoLastCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.F2 && _vm.Selected != null)
        {
            _vm.EditCommand.Execute(_vm.Selected);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _vm.Selected != null)
        {
            _vm.DeleteCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T found)
            {
                return found;
            }

            if (FindChild<T>(child) is T deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (DependencyObject? p = d; p != null; p = VisualTreeHelper.GetParent(p))
        {
            if (p is T t)
            {
                return t;
            }
        }

        return null;
    }
}
