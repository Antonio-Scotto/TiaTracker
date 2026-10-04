using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using TiaTracker.App.ViewModels;

namespace TiaTracker.App.Views;

/// <summary>
/// Le colonne delle versioni cambiano con la commessa: si costruiscono qui,
/// una DataGridTemplateColumn per versione legata a Cells[i].
/// </summary>
public partial class MatrixView : UserControl
{
    private const int FixedColumns = 2;
    private MatrixViewModel? _vm;

    public MatrixView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.ColumnsChanged -= BuildColumns;
        }

        _vm = DataContext as MatrixViewModel;
        if (_vm != null)
        {
            _vm.ColumnsChanged += BuildColumns;
            BuildColumns();
        }
    }

    private void BuildColumns()
    {
        if (_vm == null)
        {
            return;
        }

        while (Grid.Columns.Count > FixedColumns)
        {
            Grid.Columns.RemoveAt(Grid.Columns.Count - 1);
        }

        for (int i = 0; i < _vm.Columns.Count; i++)
        {
            Grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = _vm.Columns[i].Label,
                Width = new DataGridLength(118),
                CellTemplate = CellTemplate(i),
            });
        }
    }

    private static DataTemplate CellTemplate(int index)
    {
        string i = index.ToString(CultureInfo.InvariantCulture);
        string xaml =
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "  <ComboBox ItemsSource='{Binding DataContext.Options, RelativeSource={RelativeSource AncestorType=UserControl}}'" +
            "            SelectedItem='{Binding Cells[" + i + "].Option, UpdateSourceTrigger=PropertyChanged}'" +
            "            ToolTip='{Binding Cells[" + i + "].Tooltip}'" +
            "            Background='{Binding Cells[" + i + "].Option.Text, Converter={StaticResource StateBrush}}'" +
            "            BorderThickness='0' Padding='6,2' MinHeight='26' HorizontalAlignment='Stretch' />" +
            "</DataTemplate>";
        return (DataTemplate)XamlReader.Parse(xaml);
    }
}
