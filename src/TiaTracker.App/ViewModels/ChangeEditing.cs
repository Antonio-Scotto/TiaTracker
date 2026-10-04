using System.Windows;
using TiaTracker.App.Services;
using TiaTracker.App.Views;
using TiaTracker.Core.Domain;

namespace TiaTracker.App.ViewModels;

/// <summary>
/// Apertura dell'editor di modifica e salvataggio, condivisi da tutte le schede.
/// Le modifiche si creano e si scrivono dalla commessa (Modifiche, Matrice,
/// Pianificazione); dalle versioni si leggono, si associano e se ne segna lo stato.
/// </summary>
public static class ChangeEditing
{
    /// <summary>Restituisce true se l'utente ha salvato.</summary>
    public static bool Edit(Change change) => Show(change, null);

    /// <summary>
    /// Apertura da una versione: testo e blocchi in sola lettura, si salvano solo le
    /// versioni associate e i loro stati (anche di altre versioni).
    /// </summary>
    public static bool Open(Change change, ProjectVersion fromVersion) => Show(change, fromVersion);

    private static bool Show(Change change, ProjectVersion? fromVersion)
    {
        List<ProjectVersion> versions = AppServices.Versions.ByCommessa(change.CommessaId);
        List<string> names = AppServices.Snapshots.KnownNames(change.CommessaId);
        ChangeEditorViewModel vm = new(change, versions, names, fromVersion);
        ChangeEditorWindow window = new() { Owner = Application.Current.MainWindow, DataContext = vm };
        vm.Close = ok =>
        {
            window.DialogResult = ok;
        };

        if (window.ShowDialog() != true)
        {
            return false;
        }

        Change edited = vm.ToChange();
        if (fromVersion != null && AppServices.Changes.Get(change.Id) is Change current)
        {
            current.Versions = edited.Versions;
            edited = current;
        }

        AppServices.Changes.Save(edited);
        change.Id = edited.Id;
        OutputService.Refresh(edited.CommessaId);
        return true;
    }

    /// <summary>Nuova modifica della commessa: l'id se l'utente la salva, altrimenti null.</summary>
    public static long? New(long commessaId)
    {
        Change c = new() { CommessaId = commessaId };
        return Edit(c) ? c.Id : null;
    }

    /// <summary>Nuova modifica gia' compilata (es. da un task fatto): l'id se l'utente la salva, altrimenti null.</summary>
    public static long? NewPrefilled(long commessaId, long? versionId, string title, string? description, ChangeState state = ChangeState.Saved)
    {
        Change c = new() { CommessaId = commessaId, Title = title, Description = description, Date = DateOnly.FromDateTime(DateTime.Today) };
        if (versionId != null)
        {
            c.Versions.Add(new ChangeVersion { VersionId = versionId.Value, State = state });
        }

        return Edit(c) ? c.Id : null;
    }

    public static bool ConfirmDelete(Change change)
    {
        MessageBoxResult r = MessageBox.Show(
            "Eliminare la modifica \"" + change.Title + "\" con tutti i suoi stati e la sua storia?\nL'operazione non si annulla.",
            "Elimina modifica", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes)
        {
            return false;
        }

        AppServices.Changes.Delete(change.Id);
        return true;
    }
}
