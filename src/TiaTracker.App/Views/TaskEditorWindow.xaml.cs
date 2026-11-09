using System.Windows;
using TiaTracker.App.ViewModels;

namespace TiaTracker.App.Views;

public partial class TaskEditorWindow : Window
{
    private readonly TaskEditorViewModel _vm;

    public TaskEditorWindow(TaskEditorViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_vm.Validate())
        {
            DialogResult = true;
        }
    }
}
