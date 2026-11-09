using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TiaTracker.App.Controls;

/// <summary>
/// Proprieta' associate per il tema: Glyph mette un'icona (Segoe Fluent Icons)
/// davanti al contenuto di pulsanti e schede, Placeholder scrive un suggerimento
/// grigio nelle caselle di testo vuote.
/// </summary>
public static class Ui
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(Ui), new PropertyMetadata(null, OnGlyphChanged));

    public static string? GetGlyph(DependencyObject d) => (string?)d.GetValue(GlyphProperty);

    public static void SetGlyph(DependencyObject d, string? value) => d.SetValue(GlyphProperty, value);

    /// <summary>
    /// L'icona si mette nel contenuto (o nell'intestazione delle schede) e non con
    /// un template: i template Fluent di pulsanti e schede non usano ContentTemplate.
    /// Solo per contenuti di testo non legati a un binding; il testo resta come nome
    /// per UI Automation e lettori di schermo.
    /// </summary>
    private static void OnGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe)
        {
            return;
        }

        if (fe.IsLoaded)
        {
            ApplyGlyph(fe);
            return;
        }

        fe.Loaded -= OnGlyphHostLoaded;
        fe.Loaded += OnGlyphHostLoaded;
    }

    private static void OnGlyphHostLoaded(object sender, RoutedEventArgs e)
    {
        FrameworkElement fe = (FrameworkElement)sender;
        fe.Loaded -= OnGlyphHostLoaded;
        ApplyGlyph(fe);
    }

    private static void ApplyGlyph(FrameworkElement fe)
    {
        string? glyph = GetGlyph(fe);
        switch (fe)
        {
            case HeaderedContentControl h when h.Header is string header &&
                                               !System.Windows.Data.BindingOperations.IsDataBound(h, HeaderedContentControl.HeaderProperty):
                h.Header = GlyphContent(glyph, header, 13);
                SetName(h, header);
                break;
            case HeaderedContentControl h when h.Header is GlyphPanel panel:
                panel.SetGlyph(glyph);
                break;
            case System.Windows.Controls.Primitives.ButtonBase c when c.Content is string text &&
                                       !System.Windows.Data.BindingOperations.IsDataBound(c, ContentControl.ContentProperty):
                c.Content = GlyphContent(glyph, text, 14);
                SetName(c, text.Length > 0 ? text : c.ToolTip as string);
                break;
            case System.Windows.Controls.Primitives.ButtonBase c when c.Content is GlyphPanel panel:
                panel.SetGlyph(glyph);
                break;
        }
    }

    private static void SetName(DependencyObject d, string? name)
    {
        if (!string.IsNullOrEmpty(name) && string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(d)))
        {
            System.Windows.Automation.AutomationProperties.SetName(d, name);
        }
    }

    private static GlyphPanel GlyphContent(string? glyph, string text, double size)
    {
        GlyphPanel panel = new(size);
        panel.SetGlyph(glyph);
        if (text.Length > 0)
        {
            panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        }
        else
        {
            panel.Icon.Margin = new Thickness(0);
        }

        return panel;
    }

    /// <summary>Icona + testo affiancati.</summary>
    private sealed class GlyphPanel : StackPanel
    {
        public GlyphPanel(double size)
        {
            Orientation = Orientation.Horizontal;
            Icon = new TextBlock
            {
                FontSize = size,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            Children.Add(Icon);
        }

        public TextBlock Icon { get; }

        public void SetGlyph(string? glyph)
        {
            Icon.Text = glyph ?? "";
            Icon.Visibility = string.IsNullOrEmpty(glyph) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>
    /// Colonne di testo della griglia centrate in verticale (il modello Fluent della cella
    /// lascia il testo in alto, mentre tendine e chip stanno al centro). Vale per le colonne
    /// con lo stile predefinito; quelle con un ElementStyle proprio restano come sono.
    /// </summary>
    public static readonly DependencyProperty CenterCellTextProperty = DependencyProperty.RegisterAttached(
        "CenterCellText", typeof(bool), typeof(Ui), new PropertyMetadata(false, OnCenterCellTextChanged));

    public static bool GetCenterCellText(DependencyObject d) => (bool)d.GetValue(CenterCellTextProperty);

    public static void SetCenterCellText(DependencyObject d, bool value) => d.SetValue(CenterCellTextProperty, value);

    private static readonly DependencyProperty CenterHookedProperty = DependencyProperty.RegisterAttached(
        "CenterHooked", typeof(bool), typeof(Ui), new PropertyMetadata(false));

    private static void OnCenterCellTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid || e.NewValue is not true || (bool)grid.GetValue(CenterHookedProperty))
        {
            return;
        }

        grid.SetValue(CenterHookedProperty, true);
        grid.Columns.CollectionChanged += (_, _) => CenterColumns(grid);
        grid.Loaded += (_, _) => CenterColumns(grid);
        CenterColumns(grid);
    }

    private static void CenterColumns(DataGrid grid)
    {
        if (grid.TryFindResource("CellTextCentered") is not Style centered)
        {
            return;
        }

        foreach (DataGridTextColumn c in grid.Columns.OfType<DataGridTextColumn>())
        {
            if (c.ElementStyle == null || ReferenceEquals(c.ElementStyle, DataGridTextColumn.DefaultElementStyle))
            {
                c.ElementStyle = centered;
            }
        }
    }

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Ui), new PropertyMetadata(null, OnPlaceholderChanged));

    public static string? GetPlaceholder(DependencyObject d) => (string?)d.GetValue(PlaceholderProperty);

    public static void SetPlaceholder(DependencyObject d, string? value) => d.SetValue(PlaceholderProperty, value);

    private static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
        {
            return;
        }

        // Il suggerimento e' un adorner leggero: compare solo se la casella e' vuota.
        box.Loaded -= OnBoxLoaded;
        box.Loaded += OnBoxLoaded;
        box.TextChanged -= OnBoxTextChanged;
        box.TextChanged += OnBoxTextChanged;
        if (box.IsLoaded)
        {
            UpdatePlaceholder(box);
        }
    }

    private static void OnBoxLoaded(object sender, RoutedEventArgs e) => UpdatePlaceholder((TextBox)sender);

    private static void OnBoxTextChanged(object sender, TextChangedEventArgs e) => UpdatePlaceholder((TextBox)sender);

    private static void UpdatePlaceholder(TextBox box)
    {
        System.Windows.Documents.AdornerLayer? layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(box);
        if (layer == null)
        {
            return;
        }

        System.Windows.Documents.Adorner[]? existing = layer.GetAdorners(box);
        PlaceholderAdorner? adorner = existing?.OfType<PlaceholderAdorner>().FirstOrDefault();
        bool show = string.IsNullOrEmpty(box.Text) && !string.IsNullOrEmpty(GetPlaceholder(box));
        if (show && adorner != null && adorner.Text != GetPlaceholder(box))
        {
            // Suggerimento legato a un binding che e' cambiato.
            layer.Remove(adorner);
            adorner = null;
        }

        if (show && adorner == null)
        {
            layer.Add(new PlaceholderAdorner(box, GetPlaceholder(box)!));
        }
        else if (!show && adorner != null)
        {
            layer.Remove(adorner);
        }
    }

    private sealed class PlaceholderAdorner : System.Windows.Documents.Adorner
    {
        private readonly TextBlock _text;

        public PlaceholderAdorner(TextBox box, string text) : base(box)
        {
            IsHitTestVisible = false;
            _text = new TextBlock
            {
                Text = text,
                Opacity = 0.5,
                FontStyle = FontStyles.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(box.Padding.Left + 12, 0, 0, 0),
                IsHitTestVisible = false,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            AddVisualChild(_text);
        }

        public string Text => _text.Text;

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index) => _text;

        protected override Size MeasureOverride(Size constraint)
        {
            _text.Measure(AdornedElement.RenderSize);
            return AdornedElement.RenderSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _text.Arrange(new Rect(new Point(0, 0), finalSize));
            return finalSize;
        }
    }
}
