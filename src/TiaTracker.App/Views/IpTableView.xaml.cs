using System.Windows.Controls;
using System.Windows.Data;
using TiaTracker.App.ViewModels;

namespace TiaTracker.App.Views;

public partial class IpTableView : UserControl
{
    public IpTableView()
    {
        InitializeComponent();
    }

    /// <summary>Le righe lette da TIA si modificano solo nei campi dell'utente (posizione, MAC, note).</summary>
    private void OnBeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is not IpRow { IsFromTia: true } ||
            e.Column is not DataGridBoundColumn { Binding: Binding binding } ||
            !IpTableViewModel.TiaOwned.Contains(binding.Path.Path))
        {
            return;
        }

        e.Cancel = true;
        if (DataContext is IpTableViewModel vm)
        {
            vm.Status = "Campo letto da TIA: si cambia nel progetto (poi \"Aggiorna da TIA\") oppure con \"Stacca da TIA\".";
        }
    }
}
