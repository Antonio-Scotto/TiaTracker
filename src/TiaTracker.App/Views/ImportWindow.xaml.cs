using System.Windows;
using TiaTracker.App.ViewModels;

namespace TiaTracker.App.Views;

public partial class ImportWindow : Window
{
    public ImportWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ImportViewModel vm)
            {
                vm.Close = () => DialogResult = true;
            }
        };
    }
}
