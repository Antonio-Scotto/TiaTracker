using TiaTracker.App.ViewModels;

namespace TiaTracker.App.Services;

/// <summary>Esito di un lavoro: messaggio, azioni proposte nella notifica, oppure nessuna notifica.</summary>
public sealed record JobOutcome(bool Ok, string Message, IReadOnlyList<NoticeAction>? Actions = null, bool Silent = false)
{
    public static JobOutcome Done(string message, params NoticeAction[] actions) => new(true, message, actions);

    public static JobOutcome Failed(string message, params NoticeAction[] actions) => new(false, message, actions);
}

/// <summary>
/// Un lavoro lungo alla volta in tutta l'app (snapshot, export hardware,
/// documenti, catena della nuova commessa): barra in basso con testo,
/// avanzamento e Annulla; a fine lavoro una notifica con l'esito. Una catena
/// di passi e' un solo lavoro, cosi' nessuno si infila in mezzo.
/// </summary>
public static class JobRunner
{
    private static JobState? _state;
    private static Action<string>? _status;

    /// <summary>Collegato una volta dalla finestra principale.</summary>
    public static void Attach(JobState state, Action<string> setStatus)
    {
        _state = state;
        _status = setStatus;
    }

    public static bool IsRunning => _state?.Running == true || WorkerRunner.Busy;

    public static event Action? Finished;

    /// <summary>
    /// Esegue il lavoro sul thread UI (le parti pesanti vanno in Task.Run dentro <paramref name="work"/>).
    /// Restituisce null se un altro lavoro e' gia' in corso.
    /// </summary>
    public static async Task<JobOutcome?> RunAsync(string title, Func<JobState, CancellationToken, Task<JobOutcome>> work)
    {
        if (_state == null)
        {
            throw new InvalidOperationException("JobRunner non collegato");
        }

        if (IsRunning)
        {
            Notify.Warning("C'e' gia' un lavoro in corso", string.IsNullOrEmpty(_state.Text) ? null : _state.Text);
            return null;
        }

        using CancellationTokenSource cts = new();
        _state.Running = true;
        _state.Indeterminate = true;
        _state.Progress = 0;
        _state.Text = title + "...";
        _state.CancelAction = cts.Cancel;
        _status?.Invoke(title + " in corso");
        try
        {
            JobOutcome r = await work(_state, cts.Token);
            _status?.Invoke(title + (r.Ok ? ": " : " non riuscito: ") + FirstLine(r.Message));
            if (!r.Silent)
            {
                NoticeAction[] actions = r.Actions?.ToArray() ?? Array.Empty<NoticeAction>();
                if (r.Ok)
                {
                    Notify.Success(title, r.Message, actions);
                }
                else
                {
                    Notify.Error(title + " non riuscito", r.Message, actions);
                }
            }

            return r;
        }
        catch (OperationCanceledException)
        {
            _status?.Invoke(title + " annullato");
            Notify.Info(title + " annullato");
            return new JobOutcome(false, "Annullato", Silent: true);
        }
        catch (Exception ex)
        {
            Log.Error(title, ex);
            _status?.Invoke(title + " non riuscito: " + FirstLine(ex.Message));
            Notify.Error(title + " non riuscito", ex.Message);
            return new JobOutcome(false, ex.Message, Silent: true);
        }
        finally
        {
            _state.Running = false;
            _state.CancelAction = null;
            _state.Indeterminate = true;
            Finished?.Invoke();
        }
    }

    /// <summary>La barra di stato ha una riga: i messaggi lunghi di TIA restano interi nella notifica.</summary>
    private static string FirstLine(string message)
    {
        string line = message.Split('\n')[0].TrimEnd('\r');
        return line.Length <= 220 ? line : line[..217] + "...";
    }

    /// <summary>Azione "Diagnostica" per gli esiti che dipendono dall'ambiente (worker, DLL, permessi).</summary>
    public static NoticeAction DiagnosticsAction() => new("Diagnostica", Views.DiagnosticsWindow.ShowSingle);
}
