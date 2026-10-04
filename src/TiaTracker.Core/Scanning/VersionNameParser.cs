using System.Globalization;
using System.Text.RegularExpressions;

namespace TiaTracker.Core.Scanning;

public enum NameKind
{
    /// <summary>AUT123456_Demo_V0.70-V21</summary>
    CodeVersionTia,

    /// <summary>AUT260017_V21: nessuna versione di progetto, si ordina per data di salvataggio.</summary>
    CodeTia,

    /// <summary>FRT_AUT220008_28-08-2026(_V18): l'etichetta e' la data.</summary>
    Dated,

    /// <summary>Regex della radice di scansione con gruppi maj/min/tia/date.</summary>
    Custom,

    Fallback,
}

public sealed record ParsedName(
    string Label,
    NameKind Kind,
    string? ProjectCode,
    int? Major,
    int? Minor,
    int? TiaFromName,
    DateOnly? Date)
{
    /// <summary>
    /// Chiave di ordinamento come stringa confrontabile. Null quando il nome
    /// non porta un ordine: lo scanner usa allora la data di salvataggio.
    /// </summary>
    public string? SortKey
    {
        get
        {
            if (Major.HasValue)
            {
                return "V" + Major.Value.ToString("D5", CultureInfo.InvariantCulture) + "." +
                       (Minor ?? 0).ToString("D5", CultureInfo.InvariantCulture);
            }

            if (Date.HasValue)
            {
                return "D" + Date.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture) +
                       (TiaFromName.HasValue ? "." + TiaFromName.Value.ToString("D3", CultureInfo.InvariantCulture) : "");
            }

            return null;
        }
    }
}

/// <summary>
/// Dal nome della cartella (o del .rar) all'etichetta della versione. Le
/// regex si provano in ordine; la prima che combacia vince.
/// </summary>
public static class VersionNameParser
{
    private static readonly Regex AutName = new(@"^(AUT\d{6})_(.+)_V(\d+)\.(\d+)-V(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CodeTia = new(@"^(AUT\d{6})_V(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Dated = new(@"^(.+?)_(\d{2})-(\d{2})-(\d{4})(?:_V(\d+))?$", RegexOptions.CultureInvariant);
    private static readonly Regex Code = new(@"(AUT\d{6})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static ParsedName Parse(string name, string? customRegex = null)
    {
        if (!string.IsNullOrWhiteSpace(customRegex))
        {
            ParsedName? custom = TryCustom(name, customRegex);
            if (custom != null)
            {
                return custom;
            }
        }

        Match m = AutName.Match(name);
        if (m.Success)
        {
            int maj = Int(m.Groups[3].Value);
            int min = Int(m.Groups[4].Value);
            return new ParsedName(
                "V" + m.Groups[3].Value + "." + m.Groups[4].Value,
                NameKind.CodeVersionTia,
                m.Groups[1].Value.ToUpperInvariant(),
                maj,
                min,
                Int(m.Groups[5].Value),
                null);
        }

        m = CodeTia.Match(name);
        if (m.Success)
        {
            return new ParsedName(name, NameKind.CodeTia, m.Groups[1].Value.ToUpperInvariant(), null, null, Int(m.Groups[2].Value), null);
        }

        m = Dated.Match(name);
        if (m.Success && TryDate(m.Groups[4].Value, m.Groups[3].Value, m.Groups[2].Value, out DateOnly date))
        {
            int? tia = m.Groups[5].Success ? Int(m.Groups[5].Value) : null;
            string label = m.Groups[2].Value + "-" + m.Groups[3].Value + "-" + m.Groups[4].Value + (tia.HasValue ? " V" + tia.Value : "");
            return new ParsedName(label, NameKind.Dated, CodeOf(name), null, null, tia, date);
        }

        return new ParsedName(name, NameKind.Fallback, CodeOf(name), null, null, null, null);
    }

    private static ParsedName? TryCustom(string name, string pattern)
    {
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            return null;
        }

        Match m = regex.Match(name);
        if (!m.Success)
        {
            return null;
        }

        int? maj = m.Groups["maj"].Success ? Int(m.Groups["maj"].Value) : null;
        int? min = m.Groups["min"].Success ? Int(m.Groups["min"].Value) : null;
        int? tia = m.Groups["tia"].Success ? Int(m.Groups["tia"].Value) : null;
        DateOnly? date = null;
        if (m.Groups["date"].Success)
        {
            string[] formats = { "dd-MM-yyyy", "yyyy-MM-dd", "yyyyMMdd", "dd.MM.yyyy", "dd_MM_yyyy" };
            if (DateOnly.TryParseExact(m.Groups["date"].Value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d))
            {
                date = d;
            }
        }

        string label;
        if (m.Groups["label"].Success)
        {
            label = m.Groups["label"].Value;
        }
        else if (maj.HasValue)
        {
            label = "V" + m.Groups["maj"].Value + (min.HasValue ? "." + m.Groups["min"].Value : "");
        }
        else if (date.HasValue)
        {
            label = date.Value.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
        }
        else
        {
            label = name;
        }

        return new ParsedName(label, NameKind.Custom, CodeOf(name), maj, min, tia, date);
    }

    private static string? CodeOf(string name)
    {
        Match m = Code.Match(name);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static bool TryDate(string y, string mo, string d, out DateOnly date)
    {
        date = default;
        int year = Int(y), month = Int(mo), day = Int(d);
        if (month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        date = new DateOnly(year, month, day);
        return true;
    }

    private static int Int(string s) => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);

    /// <summary>
    /// Versione TIA dall'estensione: ap21 = 21, ap15_1 = 15.1.
    /// </summary>
    public static (int Major, string Text)? TiaFromExtension(string extension)
    {
        Match m = Regex.Match(extension, @"^\.?ap(\d+)(?:_(\d+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!m.Success)
        {
            return null;
        }

        int major = Int(m.Groups[1].Value);
        string text = "V" + m.Groups[1].Value + (m.Groups[2].Success ? "." + m.Groups[2].Value : "");
        return (major, text);
    }

    public static bool IsProjectExtension(string extension) => TiaFromExtension(extension) != null;
}
