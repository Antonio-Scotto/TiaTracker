namespace TiaTracker.App.Services;

public static class TaskExtensions
{
    /// <summary>
    /// Lancia un task senza aspettarlo, ma con l'errore nel log e in una notifica:
    /// con "_ = task" un'eccezione resterebbe chiusa nel Task e non la vedrebbe nessuno.
    /// </summary>
    public static async void Forget(this Task task, string context)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(context, ex);
            Notify.Error(context + " non riuscito", ex.Message);
        }
    }
}
