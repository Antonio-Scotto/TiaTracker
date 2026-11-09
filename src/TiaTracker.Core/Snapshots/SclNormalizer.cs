using System.Text.RegularExpressions;

namespace TiaTracker.Core.Snapshots;

/// <summary>Una riga del sorgente: quella mostrata e quella confrontata.</summary>
public sealed record SourceLine(string Original, string Normalized);

/// <summary>
/// Il diff SCL confronta righe normalizzate (indentazione e spazi multipli
/// non contano: TIA riesporta con indentazione diversa) ma mostra le
/// originali.
/// </summary>
public static class SclNormalizer
{
    private static readonly Regex Spaces = new(@"[ \t]+", RegexOptions.Compiled);

    public static string NormalizeLine(string line) => Spaces.Replace(line, " ").Trim();

    public static List<SourceLine> Lines(string? text)
    {
        List<SourceLine> result = new();
        if (string.IsNullOrEmpty(text))
        {
            return result;
        }

        if (text[0] == '﻿')
        {
            text = text[1..];
        }

        foreach (string raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            result.Add(new SourceLine(raw.TrimEnd('\r'), NormalizeLine(raw)));
        }

        // L'ultimo a capo non e' una riga.
        if (result.Count > 0 && result[^1].Original.Length == 0)
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    /// <summary>Righe di un XML canonico: gia' indentate in modo stabile, si confrontano cosi' come sono.</summary>
    public static List<SourceLine> XmlLines(string? canonicalXml)
    {
        List<SourceLine> result = new();
        if (string.IsNullOrEmpty(canonicalXml))
        {
            return result;
        }

        foreach (string raw in canonicalXml.Split('\n'))
        {
            result.Add(new SourceLine(raw, raw.Trim()));
        }

        return result;
    }
}
