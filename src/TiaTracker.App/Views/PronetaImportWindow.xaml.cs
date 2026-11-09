using System.Windows;
using TiaTracker.Core.Network;

namespace TiaTracker.App.Views;

/// <summary>Anteprima dell'import da PRONETA: cosa si abbina, cosa si aggiunge, cosa non torna.</summary>
public partial class PronetaImportWindow : Window
{
    private readonly List<PronetaMatch> _plan;

    public PronetaImportWindow(string file, List<PronetaMatch> plan)
    {
        InitializeComponent();
        _plan = plan;
        Header.Text = file + ": " + plan.Count + " dispositivi in rete";
        Grid.ItemsSource = plan;
        int matched = plan.Count(p => p.Kind == PronetaMatchKind.Matched);
        int differ = plan.Count(p => p.IpDiffers);
        Summary.Text = matched + " abbinati alla Lista IP, " + (plan.Count - matched) + " solo in rete" +
                       (differ > 0 ? ", " + differ + " con IP diverso" : "") + ". I PC (portatili di messa in servizio) sono esclusi: si spuntano a mano.";
    }

    private void Set(Func<PronetaMatch, bool> rule)
    {
        foreach (PronetaMatch m in _plan)
        {
            m.Apply = rule(m);
        }

        Grid.Items.Refresh();
    }

    private void OnAll(object sender, RoutedEventArgs e) => Set(_ => true);

    private void OnMatchedOnly(object sender, RoutedEventArgs e) => Set(m => m.Kind == PronetaMatchKind.Matched);

    private void OnNone(object sender, RoutedEventArgs e) => Set(_ => false);

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Grid.CommitEdit();
        DialogResult = true;
    }
}
