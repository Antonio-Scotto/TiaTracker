using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using TiaTracker.Core.Domain;

namespace TiaTracker.Core.Snapshots;

/// <summary>Hash per faccetta di un blocco, calcolati sulla forma canonica.</summary>
public sealed record BlockHashes(
    string All,
    string Code,
    string Interface,
    string Init,
    string Text,
    string Meta,
    IReadOnlyList<UnitInfo> Units);

/// <summary>
/// Dominio di hash = XML canonico (esiste per tutti i blocchi, anche LAD e
/// DB di istanza). L'SCL e' solo testo leggibile per il diff.
/// <list type="bullet">
/// <item>All: tutto il documento canonico;</item>
/// <item>Code: le reti senza commenti e titoli;</item>
/// <item>Interface: l'interfaccia senza valori iniziali e commenti;</item>
/// <item>Init: i valori iniziali (StartValue) con il loro percorso;</item>
/// <item>Text: commenti e titoli;</item>
/// <item>Meta: gli attributi del blocco tranne l'interfaccia (gia' ripuliti dai volatili).</item>
/// </list>
/// </summary>
public static class Hasher
{
    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static BlockHashes Compute(XDocument canonical)
    {
        string all = Sha256(XmlCanonicalizer.ToText(canonical));
        XElement? block = XmlCanonicalizer.BlockElement(canonical);
        if (block == null)
        {
            string empty = Sha256("");
            return new BlockHashes(all, empty, empty, empty, empty, empty, Array.Empty<UnitInfo>());
        }

        XElement? attrs = block.Element("AttributeList");
        XElement? iface = attrs?.Element("Interface");

        XElement meta = new("Meta", attrs?.Elements().Where(e => e.Name.LocalName != "Interface").Select(e => new XElement(e)));

        XElement ifaceClean = iface != null ? new XElement(iface) : new XElement("Interface");
        Strip(ifaceClean, "StartValue");
        Strip(ifaceClean, "Comment");

        List<string> initLines = new();
        if (iface != null)
        {
            foreach (XElement sv in iface.Descendants("StartValue"))
            {
                initLines.Add(PathOf(sv) + "=" + sv.Value);
            }
        }

        List<string> textLines = new();
        CollectText(block, textLines);

        XElement code = new("Code");
        List<UnitInfo> units = new();
        XElement? objects = block.Element("ObjectList");
        if (objects != null)
        {
            int index = 0;
            foreach (XElement unit in objects.Elements().Where(XmlCanonicalizer.IsCompileUnit))
            {
                index++;
                XElement unitCode = CodeOnly(unit);
                code.Add(unitCode);
                List<string> unitText = new();
                CollectText(unit, unitText);
                units.Add(new UnitInfo(
                    index,
                    TitleOf(unit),
                    Sha256(XmlCanonicalizer.ToText(unitCode)),
                    Sha256(string.Join("\n", unitText)),
                    unit.Element("AttributeList")?.Element("ProgrammingLanguage")?.Value ?? ""));
            }

            // Altro codice fuori dalle reti (raro): conta comunque.
            foreach (XElement other in objects.Elements().Where(e => !XmlCanonicalizer.IsCompileUnit(e) && e.Name.LocalName != "MultilingualText"))
            {
                code.Add(CodeOnly(other));
            }
        }

        return new BlockHashes(
            all,
            Sha256(XmlCanonicalizer.ToText(code)),
            Sha256(XmlCanonicalizer.ToText(ifaceClean)),
            Sha256(string.Join("\n", initLines)),
            Sha256(string.Join("\n", textLines)),
            Sha256(XmlCanonicalizer.ToText(meta)),
            units);
    }

    public static string UnitsJson(IReadOnlyList<UnitInfo> units) =>
        units.Count == 0 ? "[]" : JsonSerializer.Serialize(units);

    public static IReadOnlyList<UnitInfo> ParseUnits(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return Array.Empty<UnitInfo>();
        }

        return JsonSerializer.Deserialize<List<UnitInfo>>(json) ?? new List<UnitInfo>();
    }

    private static XElement CodeOnly(XElement unit)
    {
        XElement copy = new(unit);
        foreach (string name in XmlCanonicalizer.CommentElements)
        {
            Strip(copy, name);
        }

        return copy;
    }

    private static void Strip(XElement root, string localName)
    {
        foreach (XElement e in root.Descendants().Where(d => d.Name.LocalName == localName).ToList())
        {
            if (e.Parent != null)
            {
                e.Remove();
            }
        }
    }

    /// <summary>Static.motore.velocita[3]: nomi dei Member antenati e Path dei Subelement.</summary>
    private static string PathOf(XElement startValue)
    {
        List<string> parts = new();
        for (XElement? e = startValue.Parent; e != null && e.Name.LocalName != "Interface"; e = e.Parent)
        {
            if (e.Name.LocalName == "Member" || e.Name.LocalName == "Section")
            {
                parts.Add(e.Attribute("Name")?.Value ?? "?");
            }
            else if (e.Name.LocalName == "Subelement")
            {
                parts.Add("[" + (e.Attribute("Path")?.Value ?? "?") + "]");
            }
        }

        parts.Reverse();
        return string.Join(".", parts).Replace(".[", "[", StringComparison.Ordinal);
    }

    private static void CollectText(XElement root, List<string> lines)
    {
        foreach (XElement e in root.Descendants())
        {
            string n = e.Name.LocalName;
            if (n == "MultilingualTextItem")
            {
                XElement? a = e.Element("AttributeList");
                string text = a?.Element("Text")?.Value ?? "";
                if (text.Length > 0)
                {
                    lines.Add((a?.Element("Culture")?.Value ?? "") + ":" + text);
                }
            }
            else if (n == "MultiLanguageText" || (n == "Text" && e.Parent?.Name.LocalName is "LineComment" or "Comment"))
            {
                if (e.Value.Length > 0)
                {
                    lines.Add((e.Attribute("Lang")?.Value ?? "") + ":" + e.Value);
                }
            }
        }
    }

    private static string? TitleOf(XElement unit)
    {
        XElement? title = unit.Element("ObjectList")?.Elements("MultilingualText")
            .FirstOrDefault(m => m.Attribute("CompositionName")?.Value == "Title");
        return title?.Descendants("Text").Select(t => t.Value).FirstOrDefault(v => v.Length > 0);
    }
}
