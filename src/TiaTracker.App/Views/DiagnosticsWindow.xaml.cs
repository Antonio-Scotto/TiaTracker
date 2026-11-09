using System.Windows;
using TiaTracker.App.ViewModels;

namespace TiaTracker.App.Views;

public partial class DiagnosticsWindow : Window
{
    private static DiagnosticsWindow? _open;

    public DiagnosticsWindow()
    {
        InitializeComponent();
    }

    /// <summary>Una sola finestra Diagnostica: se e' gia' aperta si porta davanti e si riaggiorna.</summary>
    public static void ShowSingle()
    {
        if (_open != null)
        {
            if (_open.DataContext is DiagnosticsViewModel vm)
            {
                vm.RefreshCommand.Execute(null);
            }

            _open.Activate();
            return;
        }

        _open = new DiagnosticsWindow { Owner = Application.Current.MainWindow, DataContext = new DiagnosticsViewModel() };
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
