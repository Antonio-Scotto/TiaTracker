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

    /// <summary>Contenuti che nessuno usa piu' (snapshot o export hardware tolti).</summary>
    private const string Orphans =
        @"FROM content WHERE hash NOT IN (
              SELECT manifest_hash FROM snapshot WHERE manifest_hash IS NOT NULL
              UNION SELECT xml_ref FROM snapshot_item WHERE xml_ref IS NOT NULL
              UNION SELECT scl_ref FROM snapshot_item WHERE scl_ref IS NOT NULL
              UNION SELECT data_hash FROM hw_snapshot)";

    /// <summary>
    /// Pulizia: toglie i contenuti senza riferimenti (manifest, XML, SCL, hardware sono le
    /// sole colonne che puntano qui). Da non chiamare a meta' di un'importazione, fra il Put
    /// e l'insert di chi lo usa. Restituisce quanti e i byte compressi liberati.
    /// </summary>
    public (int Count, long Bytes) DeleteOrphans()
    {
        using Db.Tx tx = _db.Begin();
        (int count, long bytes) = _db.Query("SELECT COUNT(*) AS n, COALESCE(SUM(LENGTH(data)), 0) AS b " + Orphans,
            r => ((int)r.L("n"), r.L("b"))).Single();
        if (count > 0)
        {
            _db.Exec("DELETE " + Orphans);
        }

        tx.Commit();
        return (count, bytes);
    }
}
