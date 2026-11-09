using System.Windows;
using TiaTracker.App.ViewModels;

namespace TiaTracker.App.Views;

public partial class NewCommessaWindow : Window
{
    public NewCommessaWindow(NewCommessaViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Vm = vm;
    }

    public NewCommessaViewModel Vm { get; }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Vm.Validate())
        {
            DialogResult = true;
        }
    }
}
