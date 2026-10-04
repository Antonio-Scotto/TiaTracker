namespace TiaTracker.Core.Snapshots;

/// <summary>
/// CAS in memoria: l'ingestione (CPU) gira in background senza toccare il DB,
/// poi il contenuto si travasa nel CAS vero in una transazione.
/// </summary>
public sealed class MemoryContentStore : IContentStore
{
    private readonly Dictionary<string, (string Text, string Kind)> _items = new(StringComparer.Ordinal);
    private readonly IContentStore? _fallback;

    /// <param name="fallback">Per leggere contenuti gia' archiviati (Rehash).</param>
    public MemoryContentStore(IContentStore? fallback = null)
    {
        _fallback = fallback;
    }

    public IReadOnlyDictionary<string, (string Text, string Kind)> Items => _items;

    public string Put(string text, string kind)
    {
        string hash = Hasher.Sha256(text);
        _items.TryAdd(hash, (text, kind));
        return hash;
    }

    public string? GetText(string? hash)
    {
        if (hash == null)
        {
            return null;
        }

        return _items.TryGetValue(hash, out (string Text, string Kind) v) ? v.Text : _fallback?.GetText(hash);
    }

    public void FlushTo(IContentStore target)
    {
        foreach ((string text, string kind) in _items.Values)
        {
            target.Put(text, kind);
        }
    }
}
