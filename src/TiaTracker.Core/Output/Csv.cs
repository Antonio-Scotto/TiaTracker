using System.Text;

namespace TiaTracker.Core.Output;

/// <summary>
/// CSV per Excel in italiano: separatore ";", UTF-8 con BOM (accenti corretti
/// all'apertura con doppio clic), righe CRLF, a capo nelle celle tolti.
/// </summary>
public static class Csv
{
    public const string Separator = ";";

    public static readonly Encoding Encoding = new UTF8Encoding(true);

    public static string Quote(string? s)
    {
        string v = (s ?? "").Replace('\r', ' ').Replace('\n', ' ');
        return v.IndexOfAny(new[] { ';', '"', ',' }) >= 0 ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
    }

    public static string Line(IEnumerable<string?> cells) => string.Join(Separator, cells.Select(Quote));
}
