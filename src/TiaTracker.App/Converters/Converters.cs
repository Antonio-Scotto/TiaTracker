using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TiaTracker.Core.Domain;

namespace TiaTracker.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        return b ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility v && (v == Visibility.Visible) ^ Invert;
}

/// <summary>Visibile se il numero e' maggiore di zero (es. Found.Count).</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool has = value != null && (value is not string s || s.Length > 0);
        return has ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class UtcToLocalConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime dt)
        {
            return null;
        }

        string format = parameter as string ?? "dd/MM/yyyy HH:mm";
        DateTime utc = dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt;
        return utc.ToLocalTime().ToString(format, CultureInfo.InvariantCulture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Tono → pennello del tema (Tone.X.Bg / Fg / Solid in Themes\Tokens.xaml).
/// Parametro: "Bg" (predefinito), "Fg" o "Solid". Accetta Tone o ChipVm.
/// </summary>
public sealed class ToneBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        Tone tone = value switch
        {
            Tone t => t,
            ViewModels.ChipVm c => c.Tone,
            _ => Tone.Neutral,
        };
        return Lookup(tone, parameter as string);
    }

    public static Brush Lookup(Tone tone, string? part)
    {
        string key = "Tone." + tone + "." + (string.IsNullOrEmpty(part) ? "Bg" : part);
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Colori dei chip scritti come testo (stati delle modifiche, esiti di confronto e
/// riconciliazione, badge): il testo diventa un tono e il tono un pennello del tema.
/// Parametro come ToneBrushConverter ("Bg", "Fg", "Solid").
/// </summary>
public sealed class StateBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, Tone> Tones = new(StringComparer.OrdinalIgnoreCase)
    {
        // Versioni e badge
        ["In lavoro"] = Tone.Accent,
        ["Attiva"] = Tone.Accent,
        ["Da caricare"] = Tone.Warning,
        ["In collaudo"] = Tone.Purple,
        ["Caricata"] = Tone.Success,
        ["Sul PLC"] = Tone.Success,
        ["Vecchia"] = Tone.Neutral,
        ["Archiviata"] = Tone.Muted,
        ["Aperta in TIA"] = Tone.Orange,
        ["Aperta da altro PC"] = Tone.Purple,
        ["Lock residuo"] = Tone.Warning,
        ["Non salvata"] = Tone.Danger,
        ["Snapshot da aggiornare"] = Tone.Warning,
        ["Solo archivio"] = Tone.Muted,
        ["Mancante"] = Tone.Danger,

        // Stati delle modifiche
        ["Pianificata"] = Tone.Neutral,
        ["Importata"] = Tone.Accent,
        ["Compilata"] = Tone.Warning,
        ["Salvata"] = Tone.Success,
        ["Scartata"] = Tone.Muted,

        // Riconciliazione
        ["Confermata"] = Tone.Success,
        ["Dichiarata non trovata"] = Tone.Danger,
        ["Gia' nella base"] = Tone.Neutral,
        ["Non dichiarata"] = Tone.Warning,
        ["Ambigua"] = Tone.Purple,
        ["Ignorata"] = Tone.Muted,

        // Confronto
        ["Aggiunto"] = Tone.Success,
        ["Rimosso"] = Tone.Danger,
        ["Modificato"] = Tone.Warning,
        ["Spostato"] = Tone.Accent,
        ["Rinominato?"] = Tone.Purple,
        ["Solo ricompilato"] = Tone.Muted,
        ["Reimportato identico"] = Tone.Muted,
        ["Non confrontabile"] = Tone.Neutral,
    };

    /// <summary>Il tono di un testo di stato; null se il testo non e' uno stato noto.</summary>
    public static Tone? ToneOf(string? text)
    {
        string key = text ?? "";
        // "Compilata (non salvata)", "Solo archivio (rar)": conta la parte prima della parentesi.
        int paren = key.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0)
        {
            key = key[..paren];
        }

        return Tones.TryGetValue(key, out Tone tone) ? tone : null;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ViewModels.ChipVm chip)
        {
            return ToneBrushConverter.Lookup(chip.Tone, parameter as string);
        }

        Tone? tone = ToneOf(value?.ToString());
        if (tone == null)
        {
            return parameter as string == "Fg" ? (Application.Current?.TryFindResource("TextBrush") as Brush ?? Brushes.White) : Brushes.Transparent;
        }

        return ToneBrushConverter.Lookup(tone.Value, parameter as string);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
