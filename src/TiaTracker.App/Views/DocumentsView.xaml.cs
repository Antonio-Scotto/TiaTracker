using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using TiaTracker.App.ViewModels;
using TiaTracker.Core.Documents;

namespace TiaTracker.App.Views;

public partial class DocumentsView : UserControl
{
    private DocumentsViewModel? _vm;

    public DocumentsView()
    {
        InitializeComponent();

        // Il tema Fluent assegna gli stili di riga e cella dallo stile della DataGrid: i nostri ci si appoggiano,
        // altrimenti le righe tornano allo stile classico (bianche).
        Style row = (Style)Resources["PreviewRowStyle"];
        row.BasedOn = Grid.RowStyle ?? TryFindResource(typeof(DataGridRow)) as Style;
        Grid.RowStyle = row;
        Style cell = (Style)Resources["PreviewCellStyle"];
        cell.BasedOn = Grid.CellStyle ?? TryFindResource(typeof(DataGridCell)) as Style;
        Grid.CellStyle = cell;

        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.PreviewColumnsChanged -= RebuildColumns;
            _vm.IsShown = false;
        }

        _vm = DataContext as DocumentsViewModel;
        if (_vm == null)
        {
            return;
        }

        _vm.PreviewColumnsChanged += RebuildColumns;
        RebuildColumns();
        _vm.IsShown = IsVisible;
        if (IsVisible)
        {
            _vm.EnsureLoaded();
        }
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm == null)
        {
            return;
        }

        _vm.IsShown = IsVisible;
        if (IsVisible)
        {
            _vm.EnsureLoaded();
        }
    }

    /// <summary>Colonne della griglia dalle colonne del documento: [0], [1]... sulla riga.</summary>
    private void RebuildColumns()
    {
        Grid.Columns.Clear();
        if (_vm == null)
        {
            return;
        }

        FontFamily mono = (FontFamily)FindResource("MonoFont");
        for (int i = 0; i < _vm.PreviewColumns.Count; i++)
        {
            DocColumn c = _vm.PreviewColumns[i];
            bool editable = i == _vm.NoteColumn && _vm.CanEditRows;
            DataGridTextColumn col = new()
            {
                Header = editable ? c.Header + " ✎" : c.Header,
                Binding = new Binding("[" + i + "]") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus },
                Width = new DataGridLength(c.Weight, DataGridLengthUnitType.Star),
                MinWidth = Math.Max(64, c.Weight * 100),
            };
            if (c.Mono)
            {
                col.FontFamily = mono;
                col.FontSize = 12.5;
            }

            // Centrato in verticale anche con Consolas (altrimenti sta piu' in alto del resto della riga).
            Style element = new(typeof(TextBlock), DataGridTextColumn.DefaultElementStyle);
            element.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
            if (c.Align == DocAlign.Right)
            {
                element.Setters.Add(new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Right));
            }

            col.ElementStyle = element;

            Grid.Columns.Add(col);
        }
    }

    /// <summary>Si modificano la colonna Note e le righe aggiunte a mano; il resto viene da TIA.</summary>
    private void OnBeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (_vm == null || e.Row.Item is not DocPreviewRow row)
        {
            e.Cancel = true;
            return;
        }

        if (_vm.CanEdit(row, Grid.Columns.IndexOf(e.Column)))
        {
            return;
        }

        e.Cancel = true;
        if (!row.IsGroup)
        {
            _vm.Status = _vm.CanEditRows
                ? "Dato letto da TIA: si cambia nel progetto (poi \"Aggiorna da TIA\"). Qui si scrivono le note e le righe aggiunte."
                : _vm.PreviewHint;
        }
    }

    private void OnFilesDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFilesDrop(object sender, DragEventArgs e)
    {
        if (_vm != null && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            _vm.CopyIn(files);
        }
    }

    private void OnFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm?.SelectedFile != null)
        {
            _vm.OpenCommessaFileCommand.Execute(null);
        }
    }
}
