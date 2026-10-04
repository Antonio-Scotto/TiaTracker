using System.Globalization;
using System.Text.RegularExpressions;

namespace TiaTracker.Core.Output;

/// <summary>Nome proposto per una nuova versione creata copiando una esistente.</summary>
public static class VersionNaming
{
    private static readonly Regex AutVersion = new(@"_V(?<maj>\d+)\.(?<min>\d+)-V(?<tia>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DateSuffix = new(@"_(\d{2})-(\d{2})-(\d{4})(?<tail>(?:_V\d+)?)$", RegexOptions.CultureInvariant);

    /// <param name="existing">Nomi gia' presenti (cartelle, rar, backup): il nome proposto non li ripete.</param>
    public static string Next(string projectName, IEnumerable<string> existing, DateTime today)
    {
        HashSet<string> taken = new(existing, StringComparer.OrdinalIgnoreCase);
        string candidate;

        Match m = AutVersion.Match(projectName);
        if (m.Success)
        {
            // V0.70 -> V0.71, conservando le cifre: V0.09 -> V0.10.
            string minText = m.Groups["min"].Value;
            int min = int.Parse(minText, CultureInfo.InvariantCulture);
            do
            {
                min++;
                string next = min.ToString(CultureInfo.InvariantCulture).PadLeft(minText.Length, '0');
                candidate = projectName[..m.Index] + "_V" + m.Groups["maj"].Value + "." + next + "-V" + m.Groups["tia"].Value;
            }
            while (taken.Contains(candidate));

            return candidate;
        }

        m = DateSuffix.Match(projectName);
        if (m.Success)
        {
            string stem = projectName[..m.Index] + "_" + today.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
            candidate = stem + m.Groups["tail"].Value;
            return Unique(candidate, taken);
        }

        return Unique(projectName + "_" + today.ToString("yyyyMMdd", CultureInfo.InvariantCulture), taken);
    }

    private static string Unique(string candidate, HashSet<string> taken)
    {
        if (!taken.Contains(candidate))
        {
            return candidate;
        }

        for (int i = 2; ; i++)
        {
            string c = candidate + "_" + i.ToString(CultureInfo.InvariantCulture);
            if (!taken.Contains(c))
            {
                return c;
            }
        }
    }

    /// <summary>Un nome di cartella/progetto TIA valido: niente caratteri vietati da Windows.</summary>
    public static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name.Trim() == name && !name.EndsWith('.');
}
