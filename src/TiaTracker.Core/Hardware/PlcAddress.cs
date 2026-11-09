using System.Globalization;
using System.Text.RegularExpressions;

namespace TiaTracker.Core.Hardware;

/// <summary>
/// Un indirizzo assoluto S7: %I0.0, %IW64, %QB10, %ID100, %M5.3, %I1800.0 (UDT).
/// Byte e bit servono a ordinare la Lista IO e ad assegnare un segnale al modulo
/// che ha quel byte nella sua area di indirizzi.
/// </summary>
public readonly record struct PlcAddress(char Area, char Width, int Byte, int? Bit)
{
    private static readonly Regex Pattern = new(@"^%?\s*(?<area>[IEQAM])(?<width>[XBWDL])?\s*(?<byte>\d+)(?:\.(?<bit>[0-7]))?(?::P)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Ingresso, uscita o merker. Anche le sigle tedesche E/A diventano I/Q.</summary>
    public bool IsInput => Area == 'I';

    public bool IsOutput => Area == 'Q';

    public bool IsIo => Area is 'I' or 'Q';

    /// <summary>Byte occupati (bit e byte = 1, word = 2, dword = 4, lword = 8).</summary>
    public int Size => Width switch
    {
        'W' => 2,
        'D' => 4,
        'L' => 8,
        _ => 1,
    };

    /// <summary>Chiave d'ordinamento: prima gli ingressi, poi le uscite, poi i merker; byte e bit.</summary>
    public string SortKey => (Area switch { 'I' => "1", 'Q' => "2", _ => "3" }) +
                             Byte.ToString("D6", CultureInfo.InvariantCulture) + "." + (Bit ?? 0).ToString(CultureInfo.InvariantCulture) + Width;

    public override string ToString()
    {
        string w = Width == 'X' ? "" : Width.ToString();
        return "%" + Area + w + Byte.ToString(CultureInfo.InvariantCulture) +
               (Bit.HasValue ? "." + Bit.Value.ToString(CultureInfo.InvariantCulture) : "");
    }

    public static bool TryParse(string? text, out PlcAddress address)
    {
        address = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        Match m = Pattern.Match(text.Trim());
        if (!m.Success)
        {
            return false;
        }

        char area = char.ToUpperInvariant(m.Groups["area"].Value[0]) switch
        {
            'E' => 'I',
            'A' => 'Q',
            char c => c,
        };
        int bytePart = int.Parse(m.Groups["byte"].Value, CultureInfo.InvariantCulture);
        int? bit = m.Groups["bit"].Success ? int.Parse(m.Groups["bit"].Value, CultureInfo.InvariantCulture) : null;
        char width = m.Groups["width"].Success ? char.ToUpperInvariant(m.Groups["width"].Value[0]) : 'X';
        if (width != 'X' && bit.HasValue)
        {
            return false;
        }

        address = new PlcAddress(area, width, bytePart, bit);
        return true;
    }
}
