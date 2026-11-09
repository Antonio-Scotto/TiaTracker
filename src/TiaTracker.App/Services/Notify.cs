using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;

namespace TiaTracker.App.Services;

public enum NoticeKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>Un pulsante dentro una notifica: chiude la notifica e poi esegue l'azione.</summary>
public sealed class NoticeAction
{
    public NoticeAction(string text, Action run)
    {
        Text = text;
        Run = run;
        Command = new RelayCommand(() =>
        {
            if (Owner != null)
            {
                Notify.Dismiss(Owner);
            }

            Run();
        });
    }

    public string Text { get; }

    public Action Run { get; }

    public Notice? Owner { get; internal set; }

    public ICommand Command { get; }
}

/// <summary>Una notifica: barra in alto (Banner, resta finche' il problema c'e') o avviso in basso a destra.</summary>
public sealed class Notice
{
    public Notice(NoticeKind kind, string title, string? message, bool banner, IReadOnlyList<NoticeAction> actions)
    {
        Kind = kind;
        Title = title;
        Message = message;
        IsBanner = banner;
        Actions = actions;
        foreach (NoticeAction a in actions)
        {
            a.Owner = this;
        }

        CloseCommand = new RelayCommand(() => Notify.Dismiss(this));
    }

    public NoticeKind Kind { get; }

    public string Title { get; }

    public string? Message { get; }

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    public bool IsBanner { get; }

    /// <summary>Chiave per sostituire una notifica dello stesso tipo invece di accumularle.</summary>
    public string? Key { get; init; }

    public IReadOnlyList<NoticeAction> Actions { get; }

    public ICommand CloseCommand { get; }

    /// <summary>Glifo Segoe Fluent Icons per il tipo.</summary>
    public string Glyph => Kind switch
    {
        NoticeKind.Success => "",
        NoticeKind.Warning => "",
        NoticeKind.Error => "",
        _ => "",
    };
}

/// <summary>
/// Notifiche non bloccanti al posto dei MessageBox informativi. Successi e
/// informazioni spariscono da soli, avvisi ed errori restano finche' non si
/// chiudono. Le conferme (si'/no) restano finestre di dialogo.
/// </summary>
public static class Notify
{
    private static readonly TimeSpan AutoClose = TimeSpan.FromSeconds(8);

    public static ObservableCollection<Notice> Toasts { get; } = new();

    public static ObservableCollection<Notice> Banners { get; } = new();

    public static Notice Info(string title, string? message = null, params NoticeAction[] actions) =>
        Show(NoticeKind.Info, title, message, false, null, actions);

    public static Notice Success(string title, string? message = null, params NoticeAction[] actions) =>
        Show(NoticeKind.Success, title, message, false, null, actions);

    public static Notice Warning(string title, string? message = null, params NoticeAction[] actions) =>
        Show(NoticeKind.Warning, title, message, false, null, actions);

    public static Notice Error(string title, string? message = null, params NoticeAction[] actions) =>
        Show(NoticeKind.Error, title, message, false, null, actions);

    /// <summary>Barra persistente in alto, una per chiave (si sostituisce).</summary>
    public static Notice Banner(string key, NoticeKind kind, string title, string? message = null, params NoticeAction[] actions) =>
        Show(kind, title, message, true, key, actions);

    public static Notice Show(NoticeKind kind, string title, string? message, bool banner, string? key, IReadOnlyList<NoticeAction> actions)
    {
        Notice n = new(kind, title, message, banner, actions) { Key = key };
        Dispatcher d = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (!d.CheckAccess())
        {
            d.BeginInvoke(() => Add(n));
            return n;
        }

        Add(n);
        return n;
    }

    public static void Dismiss(Notice n)
    {
        Toasts.Remove(n);
        Banners.Remove(n);
    }

    public static void DismissKey(string key)
    {
        foreach (Notice n in Banners.Where(b => b.Key == key).Concat(Toasts.Where(t => t.Key == key)).ToList())
        {
            Dismiss(n);
        }
    }

    private static void Add(Notice n)
    {
        if (n.Key != null)
        {
            DismissKey(n.Key);
        }

        if (n.IsBanner)
        {
            Banners.Add(n);
            return;
        }

        Toasts.Add(n);
        while (Toasts.Count > 4)
        {
            Toasts.RemoveAt(0);
        }

        Log.Info("Notifica " + n.Kind + ": " + n.Title + (n.HasMessage ? " - " + n.Message : ""));
        if (n.Kind is NoticeKind.Info or NoticeKind.Success)
        {
            DispatcherTimer timer = new() { Interval = AutoClose };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Toasts.Remove(n);
            };
            timer.Start();
        }
    }
}
