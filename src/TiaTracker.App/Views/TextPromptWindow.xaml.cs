using System.Windows;

namespace TiaTracker.App.Views;

public partial class TextPromptWindow : Window
{
    public TextPromptWindow()
    {
        InitializeComponent();
    }

    /// <summary>Null se l'utente annulla.</summary>
    public static string? Ask(string title, string message, string initial)
    {
        TextPromptWindow w = new() { Owner = Application.Current.MainWindow, Title = title };
        w.Message.Text = message;
        w.Input.Text = initial;
        w.Input.SelectAll();
        w.Loaded += (_, _) => w.Input.Focus();
        return w.ShowDialog() == true ? w.Input.Text : null;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
