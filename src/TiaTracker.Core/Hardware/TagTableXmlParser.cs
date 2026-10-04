using System.Xml.Linq;
using TiaTracker.Contracts;

namespace TiaTracker.Core.Hardware;

/// <summary>
/// Una tabella dei tag esportata da Openness (SW.Tags.PlcTagTable): nome, tipo,
/// indirizzo logico e commento di ogni tag. Si confrontano i nomi locali, senza
/// namespace, come per gli XML dei blocchi.
/// </summary>
public static class TagTableXmlParser
{
    /// <param name="culture">Lingua preferita del commento (es. it-IT); se manca, il primo testo non vuoto.</param>
    public static (string? TableName, List<HwTag> Tags) Parse(string xml, string? culture)
    {
        XDocument doc = XDocument.Parse(xml);
        XElement? table = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "SW.Tags.PlcTagTable");
        if (table == null)
        {
            return (null, new List<HwTag>());
        }

        string? name = Attribute(table, "Name");
        List<HwTag> tags = new();
        foreach (XElement tag in table.Descendants().Where(e => e.Name.LocalName == "SW.Tags.PlcTag"))
        {
            tags.Add(new HwTag
            {
                Name = Attribute(tag, "Name"),
                DataType = Attribute(tag, "DataTypeName"),
                Address = Attribute(tag, "LogicalAddress"),
                Comment = Comment(tag, culture),
            });
        }

        return (name, tags);
    }

    /// <summary>Valore di un elemento della AttributeList diretta dell'oggetto.</summary>
    private static string? Attribute(XElement owner, string name)
    {
        XElement? list = owner.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
        string? value = list?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? Comment(XElement tag, string? culture)
    {
        XElement? objects = tag.Elements().FirstOrDefault(e => e.Name.LocalName == "ObjectList");
        XElement? text = objects?.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "MultilingualText" && (string?)e.Attribute("CompositionName") == "Comment");
        if (text == null)
        {
            return null;
        }

        string? first = null;
        foreach (XElement item in text.Descendants().Where(e => e.Name.LocalName == "MultilingualTextItem"))
        {
            string? value = Attribute(item, "Text");
            if (value == null)
            {
                continue;
            }

            if (culture != null && string.Equals(Attribute(item, "Culture"), culture, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            first ??= value;
        }

        return first;
    }
}
