using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TiaTracker.Core.Snapshots;

namespace TiaTracker.Data;

/// <summary>
/// Archivio indirizzato per contenuto: chiave SHA-256 del testo, dati
/// compressi Brotli. Lo stesso XML in due snapshot occupa spazio una volta.
/// </summary>
public sealed class ContentStore : IContentStore
{
    private readonly Db _db;

    public ContentStore(Db db)
    {
        _db = db;
    }

    public string Put(string text, string kind)
    {
        byte[] raw = Encoding.UTF8.GetBytes(text);
        string hash = Convert.ToHexStringLower(SHA256.HashData(raw));

        object? exists = _db.Scalar("SELECT 1 FROM content WHERE hash = $h", ("$h", hash));
        if (exists != null)
        {
            return hash;
        }

        using MemoryStream ms = new();
        using (BrotliStream brotli = new(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            brotli.Write(raw);
        }

        _db.Exec("INSERT OR IGNORE INTO content (hash, kind, size, data) VALUES ($h, $k, $s, $d)",
            ("$h", hash), ("$k", kind), ("$s", raw.Length), ("$d", ms.ToArray()));
        return hash;
    }

    public string? GetText(string? hash)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return null;
        }

        object? data = _db.Scalar("SELECT data FROM content WHERE hash = $h", ("$h", hash));
        if (data is not byte[] bytes)
        {
            return null;
        }

        using MemoryStream input = new(bytes);
        using BrotliStream brotli = new(input, CompressionMode.Decompress);
        using StreamReader reader = new(brotli, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
