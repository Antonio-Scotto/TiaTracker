using System.Globalization;
using System.Windows;
using TiaTracker.Core.Planning;

namespace TiaTracker.App.Views;

/// <summary>Riepilogo di uno spostamento con cascata o conflitti: conferma e motivo per lo storico.</summary>
public partial class ReasonWindow : Window
{
    private static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

    public ReasonWindow(ScheduleResult result, string action, string? reason)
    {
        InitializeComponent();
        int others = result.Changes.Count(c => c.Cascade);
        Heading.Text = action;
        Intro.Text = (others switch
        {
            0 => "",
            1 => "Sposta anche 1 altra attivita' che ne dipende. ",
            _ => "Sposta anche " + others + " altre attivita' che ne dipendono. ",
        }) + (result.Conflicts.Count > 0 ? "Alcuni vincoli non si possono rispettare (sotto)." : "Durate conservate in giorni lavorativi.");
        ChangesGrid.ItemsSource = result.Changes.Select(c => new
        {
            c.Title,
            From = Range(c.OldStart, c.OldEnd),
            To = Range(c.NewStart, c.NewEnd),
            Shift = c.ShiftDays == 0 ? (c.Resized ? "durata" : "") : (c.ShiftDays > 0 ? "+" : "") + c.ShiftDays + " gg",
            Origin = c.Cascade ? "cascata" : "a mano",
        }).ToList();
        if (result.Conflicts.Count > 0)
        {
            Conflicts.ItemsSource = result.Conflicts;
            ConflictsPanel.Visibility = Visibility.Visible;
        }

        ReasonBox.Text = reason ?? "";
        Loaded += (_, _) => ReasonBox.Focus();
    }

    /// <summary>Il motivo scritto (null se vuoto).</summary>
    public string? Reason => ReasonBox.Text.Trim().Length == 0 ? null : ReasonBox.Text.Trim();

    private static string Range(DateOnly s, DateOnly e) =>
        s == e ? s.ToString("ddd dd/MM/yy", It) : s.ToString("dd/MM", It) + " - " + e.ToString("dd/MM/yy", It);

    private void OnApply(object sender, RoutedEventArgs e) => DialogResult = true;
}
