using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using TiaTracker.App.Controls;

namespace TiaTracker.App.Services;

/// <summary>
/// Lingua dell'interfaccia. I testi sono scritti in italiano nel codice e nello XAML;
/// in inglese si traducono al volo con Assets\Lang\en.json (chiave = testo italiano).
/// Si applica all'avvio: cambiare lingua richiede un riavvio. In italiano non fa niente.
/// I documenti generati (DOCX/PDF/CSV) restano in italiano.
/// </summary>
public static class Loc
{
    public const string Italian = "it";
    public const string English = "en";

    private static readonly Dictionary<string, string> Exact = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> Lower = new(StringComparer.Ordinal);
    private static readonly List<(Regex Regex, string Target, int[] Order)> Patterns = new();
    private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);
    private static readonly Regex Placeholder = new(@"\{(\d+)\}", RegexOptions.Compiled);
    private static readonly string[] Separators = { "\r\n", "\n", "  ·  ", " · ", " → ", " - ", " — ", "; ", ": ", ", ", " (", ")" };

    private static readonly DependencyProperty DoneProperty = DependencyProperty.RegisterAttached(
        "LocDone", typeof(bool), typeof(Loc), new PropertyMetadata(false));

    private static readonly HashSet<string> Missing = new(StringComparer.Ordinal);

    /// <summary>Per completare il dizionario: TIATRACKER_LOC_MISSING=file scrive i testi senza traduzione alla chiusura.</summary>
    private static readonly string? MissingLog = Environment.GetEnvironmentVariable("TIATRACKER_LOC_MISSING");

    public static string Language { get; private set; } = Italian;

    public static void WriteMissing()
    {
        if (MissingLog != null && Missing.Count > 0)
        {
            File.WriteAllText(MissingLog, JsonSerializer.Serialize(Missing.Order(StringComparer.Ordinal).ToList(),
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
    }

    public static bool IsEnglish => Language == English;

    /// <summary>Lingua dalle impostazioni; la prima volta quella di Windows (italiano o inglese).</summary>
    public static string Resolve(string? setting)
    {
        if (setting == Italian || setting == English)
        {
            return setting;
        }

        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == Italian ? Italian : English;
    }

    public static void Start(string language)
    {
        Language = language;
        if (!IsEnglish)
        {
            return;
        }

        LoadDictionary();

        // Il Loaded "broadcast" di WPF salta gli elementi senza gestori propri (i TextBlock):
        // si percorre l'albero visuale di ogni finestra dopo i layout, al massimo ogni 150 ms.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded), true);
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnPopupOpened), true);
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler(OnPopupOpened), true);
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window w || (bool)w.GetValue(DoneProperty))
        {
            return;
        }

        Walk(w);
        bool pending = false;
        DateTime last = DateTime.MinValue;
        w.LayoutUpdated += (_, _) =>
        {
            if (pending)
            {
                return;
            }

            pending = true;
            TimeSpan wait = TimeSpan.FromMilliseconds(150) - (DateTime.UtcNow - last);
            DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(1) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                pending = false;
                last = DateTime.UtcNow;
                Walk(w);
            };
            timer.Start();
        };
    }

    private static void OnPopupOpened(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject d)
        {
            Walk(d);
        }
    }

    private static void Walk(DependencyObject root)
    {
        Stack<DependencyObject> todo = new();
        todo.Push(root);
        while (todo.Count > 0)
        {
            DependencyObject d = todo.Pop();
            if (d is FrameworkElement fe && !(bool)fe.GetValue(DoneProperty))
            {
                fe.SetValue(DoneProperty, true);
                try
                {
                    Apply(fe);
                }
                catch (Exception ex)
                {
                    Log.Warn("Traduzione di " + fe.GetType().Name + ": " + ex.Message);
                }
            }

            if (d is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                int n = VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < n; i++)
                {
                    todo.Push(VisualTreeHelper.GetChild(d, i));
                }
            }
        }
    }

    /// <summary>Testo nella lingua scelta (in italiano lo stesso testo).</summary>
    public static string T(string? text)
    {
        if (string.IsNullOrEmpty(text) || !IsEnglish)
        {
            return text ?? "";
        }

        if (Cache.TryGetValue(text, out string? hit))
        {
            return hit;
        }

        string? translated = Translate(text, 0);
        string result = translated ?? text;
        Cache[text] = result;
        if (translated == null && MissingLog != null && text.Any(char.IsLetter))
        {
            Missing.Add(text);
        }
        return result;
    }

    /// <summary>Un testo per lingua, per i contenuti scritti apposta (guida, avvisi della lingua).</summary>
    public static string Pick(string italian, string english) => IsEnglish ? english : italian;

    private static void LoadDictionary()
    {
        try
        {
            using Stream? s = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Lang/en.json"))?.Stream;
            if (s == null)
            {
                Log.Warn("Dizionario inglese non trovato");
                return;
            }

            Dictionary<string, string>? map = JsonSerializer.Deserialize<Dictionary<string, string>>(s);
            foreach ((string it, string en) in map ?? new())
            {
                string key = it.Trim();
                if (key.Length == 0 || en.Length == 0)
                {
                    continue;
                }

                if (Placeholder.IsMatch(key))
                {
                    AddPattern(key, en.Trim());
                }
                else
                {
                    Exact[key] = en.Trim();
                    if (key.Any(char.IsLower))
                    {
                        Lower[key.ToLowerInvariant()] = en.Trim();
                    }
                }
            }

            // I modelli piu' lunghi (piu' specifici) prima.
            Patterns.Sort((a, b) => b.Regex.ToString().Length.CompareTo(a.Regex.ToString().Length));
        }
        catch (Exception ex)
        {
            Log.Error("Dizionario inglese illeggibile", ex);
        }
    }

    private static void AddPattern(string key, string target)
    {
        MatchCollection marks = Placeholder.Matches(key);
        if (marks.Count == 0 || Regex.Replace(key, @"\{\d+\}", "").Trim().Length < 2)
        {
            return;
        }

        string pattern = "^" + Placeholder.Replace(Regex.Escape(key).Replace(@"\{", "{"), "(.+?)") + "$";
        int[] order = marks.Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
        try
        {
            Patterns.Add((new Regex(pattern, RegexOptions.Singleline | RegexOptions.Compiled), target, order));
        }
        catch (ArgumentException)
        {
        }
    }

    private static string? Translate(string text, int depth)
    {
        if (depth > 6)
        {
            return null;
        }

        string core = text.Trim();
        if (core.Length == 0 || !core.Any(char.IsLetter))
        {
            return null;
        }

        string lead = text[..text.IndexOf(core[0])];
        string trail = text[(lead.Length + core.Length)..];

        string? found = Lookup(core, depth);
        return found == null ? null : lead + found + trail;
    }

    private static string? Lookup(string core, int depth)
    {
        if (Exact.TryGetValue(core, out string? exact))
        {
            return exact;
        }

        // Punteggiatura finale (":", "...", "?") staccata dalla chiave.
        Match punct = Regex.Match(core, @"^(.*?[^\s.:?!])([.:?!]+|\.\.\.)$", RegexOptions.Singleline);
        if (punct.Success && Exact.TryGetValue(punct.Groups[1].Value, out string? bare))
        {
            return bare + punct.Groups[2].Value;
        }

        foreach ((Regex regex, string target, int[] order) in Patterns)
        {
            Match m = regex.Match(core);
            if (!m.Success)
            {
                continue;
            }

            string[] values = new string[order.Max() + 1];
            for (int i = 0; i < order.Length; i++)
            {
                string v = m.Groups[i + 1].Value;
                values[order[i]] = Translate(v, depth + 1) ?? v;
            }

            return Placeholder.Replace(target, mm =>
            {
                int n = int.Parse(mm.Groups[1].Value, CultureInfo.InvariantCulture);
                return n < values.Length ? values[n] ?? "" : mm.Value;
            });
        }

        // Numero davanti: "5 versioni".
        Match num = Regex.Match(core, @"^([\d.,]+\s+)(.+)$", RegexOptions.Singleline);
        if (num.Success && Translate(num.Groups[2].Value, depth + 1) is { } counted)
        {
            return num.Groups[1].Value + counted;
        }

        // Testi composti: si traducono i pezzi.
        foreach (string sep in Separators)
        {
            int at = core.IndexOf(sep, StringComparison.Ordinal);
            if (at <= 0)
            {
                continue;
            }

            string left = core[..at];
            string right = core[(at + sep.Length)..];
            string? l = Translate(left, depth + 1);
            string? r = right.Length == 0 ? "" : Translate(right, depth + 1);
            if (l != null || (r != null && r.Length > 0))
            {
                return (l ?? left) + sep + (r ?? right);
            }
        }

        return Lower.TryGetValue(core.ToLowerInvariant(), out string? lower) ? MatchCase(core, lower) : null;
    }

    private static string MatchCase(string source, string translated)
    {
        if (translated.Length == 0)
        {
            return translated;
        }

        if (source.Any(char.IsLetter) && !source.Any(char.IsLower))
        {
            return translated.ToUpper(CultureInfo.InvariantCulture);
        }

        if (char.IsUpper(source[0]))
        {
            return char.ToUpper(translated[0], CultureInfo.InvariantCulture) + translated[1..];
        }

        // A meta' frase in minuscolo, tranne le sigle (PLC, TIA, IP).
        bool acronym = translated.Length > 1 && char.IsUpper(translated[1]);
        return char.IsLower(source[0]) && !acronym ? char.ToLower(translated[0], CultureInfo.InvariantCulture) + translated[1..] : translated;
    }

    // ---- Albero visuale: testi fissi tradotti, testi in binding passati dal convertitore ----

    private static void Apply(FrameworkElement fe)
    {
        Prop(fe, FrameworkElement.ToolTipProperty);
        Prop(fe, AutomationProperties.NameProperty);
        Prop(fe, Ui.PlaceholderProperty);

        switch (fe)
        {
            case Window w:
                Prop(w, Window.TitleProperty);
                break;
            case TextBlock tb:
                // Text fisso o Run: si traducono i Run (mai Text, che cancellerebbe i Run in binding).
                if (tb.ReadLocalValue(TextBlock.TextProperty) is BindingExpression)
                {
                    Prop(tb, TextBlock.TextProperty);
                }
                else
                {
                    Inlines(tb.Inlines);
                }

                break;
            case DataGrid grid:
                foreach (DataGridColumn c in grid.Columns)
                {
                    if (c.Header is string h)
                    {
                        c.Header = T(h);
                    }
                }

                break;
            case ListView { View: GridView gv }:
                foreach (GridViewColumn c in gv.Columns)
                {
                    if (c.Header is string h)
                    {
                        c.Header = T(h);
                    }
                }

                break;
        }

        if (fe is HeaderedContentControl or HeaderedItemsControl)
        {
            Prop(fe, fe is HeaderedContentControl ? HeaderedContentControl.HeaderProperty : HeaderedItemsControl.HeaderProperty);
        }

        if (fe is ContentControl cc && fe is not Window)
        {
            Prop(cc, ContentControl.ContentProperty);
        }

        if (fe is ComboBox combo)
        {
            foreach (object item in combo.Items)
            {
                if (item is ComboBoxItem ci && ci.Content is string s)
                {
                    ci.Content = T(s);
                }
            }
        }
    }

    private static void Inlines(InlineCollection inlines)
    {
        foreach (Inline inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    Prop(run, Run.TextProperty);
                    break;
                case Span span:
                    Inlines(span.Inlines);
                    break;
            }
        }
    }

    /// <summary>Valore locale fisso: tradotto; binding: rifatto con il convertitore che traduce.</summary>
    private static void Prop(DependencyObject d, DependencyProperty dp)
    {
        object local = d.ReadLocalValue(dp);
        if (local is string s)
        {
            string t = T(s);
            if (!ReferenceEquals(t, s) && t != s)
            {
                d.SetValue(dp, t);
            }

            return;
        }

        if (local is BindingExpression { ParentBinding: { } b } && b.Converter == null && d is FrameworkElement or FrameworkContentElement)
        {
            Binding copy = Clone(b);
            if (d is FrameworkElement fe)
            {
                fe.SetBinding(dp, copy);
            }
            else
            {
                ((FrameworkContentElement)d).SetBinding(dp, copy);
            }
        }
    }

    private static Binding Clone(Binding b)
    {
        Binding c = new()
        {
            Mode = b.Mode,
            UpdateSourceTrigger = b.UpdateSourceTrigger,
            Converter = Translator.Instance,
            StringFormat = b.StringFormat == null ? null : T(b.StringFormat),
            FallbackValue = b.FallbackValue,
            TargetNullValue = b.TargetNullValue is string tn ? T(tn) : b.TargetNullValue,
            IsAsync = b.IsAsync,
            BindsDirectlyToSource = b.BindsDirectlyToSource,
            NotifyOnSourceUpdated = b.NotifyOnSourceUpdated,
            NotifyOnTargetUpdated = b.NotifyOnTargetUpdated,
            Delay = b.Delay,
        };
        if (b.Path != null)
        {
            c.Path = b.Path;
        }

        if (b.XPath != null)
        {
            c.XPath = b.XPath;
        }

        if (b.ElementName != null)
        {
            c.ElementName = b.ElementName;
        }
        else if (b.RelativeSource != null)
        {
            c.RelativeSource = b.RelativeSource;
        }
        else if (b.Source != null)
        {
            c.Source = b.Source;
        }

        return c;
    }

    private sealed class Translator : IValueConverter
    {
        public static readonly Translator Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is string s ? T(s) : value;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value;
    }
}

/// <summary>MessageBox con testo e titolo nella lingua scelta.</summary>
public static class Msg
{
    public static MessageBoxResult Show(string text) => MessageBox.Show(Loc.T(text));

    public static MessageBoxResult Show(string text, string caption) => MessageBox.Show(Loc.T(text), Loc.T(caption));

    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button) =>
        MessageBox.Show(Loc.T(text), Loc.T(caption), button);

    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage icon) =>
        MessageBox.Show(Loc.T(text), Loc.T(caption), button, icon);

    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult def) =>
        MessageBox.Show(Loc.T(text), Loc.T(caption), button, icon, def);

    public static MessageBoxResult Show(Window owner, string text, string caption, MessageBoxButton button, MessageBoxImage icon) =>
        MessageBox.Show(owner, Loc.T(text), Loc.T(caption), button, icon);

    public static MessageBoxResult Show(Window owner, string text, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult def) =>
        MessageBox.Show(owner, Loc.T(text), Loc.T(caption), button, icon, def);

    public static MessageBoxResult Show(Window owner, string text, string caption, MessageBoxButton button) =>
        MessageBox.Show(owner, Loc.T(text), Loc.T(caption), button);

    public static MessageBoxResult Show(Window owner, string text, string caption) =>
        MessageBox.Show(owner, Loc.T(text), Loc.T(caption));
}
