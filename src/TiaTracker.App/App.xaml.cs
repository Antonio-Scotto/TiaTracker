using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using TiaTracker.App.Services;
using TiaTracker.App.ViewModels;
using TiaTracker.App.Views;

namespace TiaTracker.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        ExtendThemeStyles();

        try
        {
            AppServices.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Avvio fallito", ex);
            MessageBox.Show("Impossibile aprire l'archivio in " + DataPaths.Root + ":\n\n" + ex.Message,
                "TiaTracker", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        MainViewModel vm = new();
        MainWindow window = new() { DataContext = vm };
        MainWindow = window;
        window.Show();
        vm.Load();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("Chiusura");
        AppServices.Stop();
        base.OnExit(e);
    }

    /// <summary>
    /// Stili impliciti che estendono quelli del tema Fluent. Si creano qui, a tema
    /// gia' caricato: in App.xaml un BasedOn="{StaticResource {x:Type Button}}" su
    /// uno stile implicito salta i dizionari dell'applicazione (dove sta Fluent) e
    /// prende lo stile classico di Windows.
    /// </summary>
    private void ExtendThemeStyles()
    {
        Extend(typeof(TabItem), null, new Setter(Control.PaddingProperty, new Thickness(12, 6, 12, 6)));

        // La cella selezionata di Fluent scuro e' azzurro chiaro (DataGridRowSelected*ThemeBrush nel
        // modello della cella): nelle griglie, e solo li', una selezione blu scura con testo chiaro.
        ResourceDictionary gridColors = new()
        {
            ["DataGridRowSelectedBackgroundThemeBrush"] = Frozen(Color.FromRgb(0x27, 0x45, 0x75)),
            ["DataGridRowSelectedForegroundThemeBrush"] = Frozen(Colors.White),
        };
        Extend(typeof(DataGrid), gridColors,
            new Setter(TiaTracker.App.Controls.Ui.CenterCellTextProperty, true),
            new Setter(Control.BackgroundProperty, Brushes.Transparent),
            new Setter(DataGrid.RowBackgroundProperty, Brushes.Transparent),
            new Setter(DataGrid.AlternatingRowBackgroundProperty, Resources["RowAltBrush"]),
            new Setter(DataGrid.GridLinesVisibilityProperty, DataGridGridLinesVisibility.Horizontal),
            new Setter(DataGrid.HorizontalGridLinesBrushProperty, Resources["GridLineBrush"]),
            new Setter(DataGrid.HeadersVisibilityProperty, DataGridHeadersVisibility.Column),
            new Setter(Control.BorderThicknessProperty, new Thickness(0)),
            new Setter(DataGrid.MinRowHeightProperty, Resources["RowHeight"]));
        Extend(typeof(DataGridColumnHeader), null,
            new Setter(Control.FontWeightProperty, FontWeights.SemiBold),
            new Setter(Control.ForegroundProperty, Resources["MutedBrush"]));
    }

    private void Extend(Type type, ResourceDictionary? resources, params Setter[] setters)
    {
        Style style = new(type, TryFindResource(type) as Style);
        foreach (Setter s in setters)
        {
            style.Setters.Add(s);
        }

        if (resources != null)
        {
            style.Resources = resources;
        }

        style.Seal();
        Resources[type] = style;
    }

    private static SolidColorBrush Frozen(Color c)
    {
        SolidColorBrush b = new(c);
        b.Freeze();
        return b;
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Eccezione non gestita", e.Exception);
        MessageBox.Show(e.Exception.Message, "TiaTracker - errore", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
