using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Messaging;
using TiaTracker.App.Services;
using TiaTracker.App.ViewModels;
using TiaTracker.Core.Domain;

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

    private bool _waitingForJob;
    private bool _canClose;

    /// <summary>
    /// Chiudendo con un lavoro in corso il worker (e il TIA senza interfaccia che ha aperto)
    /// resterebbe vivo da solo, con la copia di lavoro occupata: si chiede, si annulla, si
    /// aspetta la fine e poi si chiude. Le modifiche della Lista IP ancora in attesa si salvano.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_waitingForJob)
        {
            // Gia' in attesa della fine del lavoro: il secondo clic su X non fa niente.
            e.Cancel = true;
        }
        else if (!_canClose && JobRunner.IsRunning)
        {
            string? text = JobRunner.CurrentText;
            e.Cancel = true;
            if (MessageBox.Show(this,
                    "C'e' un lavoro in corso" + (string.IsNullOrEmpty(text) ? "" : ":\n" + text) +
                    "\n\nAnnullarlo e chiudere TiaTracker?",
                    "Chiudi TiaTracker", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
            {
                _waitingForJob = true;
                CloseAfterJob().Forget("Chiusura");
            }
        }

        if (!e.Cancel)
        {
            foreach (Commessa c in AppServices.Commesse.All())
            {
                WeakReferenceMessenger.Default.Send(new IpTableFlushRequest(c.Id));
            }
        }

        base.OnClosing(e);
    }

    private async Task CloseAfterJob()
    {
        if (DataContext is MainViewModel vm)
        {
            vm.StatusText = "Chiusura: annullamento del lavoro in corso...";
        }

        JobRunner.CancelCurrent();

        // Il worker ha 15 s per fermarsi da solo, poi si chiude d'ufficio: oltre 40 s si esce comunque
        // (il worker vede chiudersi stdin e si ferma lo stesso).
        DateTime limit = DateTime.UtcNow.AddSeconds(40);
        while (JobRunner.IsRunning && DateTime.UtcNow < limit)
        {
            await Task.Delay(250);
        }

        if (JobRunner.IsRunning)
        {
            Log.Warn("Chiusura con un lavoro ancora in corso dopo 40 s");
        }

        _waitingForJob = false;
        _canClose = true;
        Close();
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
