using System.Windows.Controls;
using System.Windows.Input;
using TiaTracker.App.ViewModels;

namespace TiaTracker.App.Views;

public partial class CommessaView : UserControl
{
    public CommessaView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Griglia delle modifiche da tastiera: Invio o F2 apre, Canc elimina (con la conferma
    /// solita). In anteprima: il DataGrid si tiene Invio per scendere di riga.
    /// </summary>
    private void OnChangesKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not CommessaDetailViewModel vm || Keyboard.Modifiers != ModifierKeys.None || vm.SelectedChange == null)
        {
            return;
        }

        ICommand? command = e.Key switch
        {
            Key.Enter or Key.F2 => vm.EditChangeCommand,
            Key.Delete => vm.DeleteChangeCommand,
            _ => null,
        };
        if (command != null && command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }
    }
}
