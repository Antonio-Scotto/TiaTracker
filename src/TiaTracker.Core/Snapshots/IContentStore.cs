namespace TiaTracker.Core.Snapshots;

/// <summary>Archivio indirizzato per contenuto (implementato nel progetto Data).</summary>
public interface IContentStore
{
    /// <summary>Salva il testo se non c'e' gia' e ne restituisce l'hash SHA-256.</summary>
    string Put(string text, string kind);

    string? GetText(string? hash);
}
