using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TiaTracker.Core.Snapshots;

/// <summary>
/// Forma canonica degli XML esportati da TIA, perche' due export dello
/// stesso blocco non modificato diano lo stesso hash. TIA riesporta con
/// indentazione diversa e mette campi volatili:
/// <list type="bullet">
/// <item>Engineering e DocumentInfo (data di export, prodotti installati): via;</item>
/// <item>namespace con suffisso di versione Openness (…/Interface/v5): via, conta il nome locale;</item>
/// <item>attributi ID degli oggetti: via;</item>
/// <item>UId: rinumerati per ordine di comparsa dentro ogni rete (le Wire LAD li usano, non si tolgono);</item>
/// <item>Blank (spazi SCL) via, Num di NewLine via, NewLine consecutivi fusi;</item>
/// <item>spazi collassati solo nei testi dei commenti;</item>
/// <item>attributi ordinati per nome;</item>
/// <item>date, IsConsistent, memorie e Number con AutoNumber tolti dagli attributi del blocco.</item>
/// </list>
/// Cambiare queste regole cambia gli hash: alzare <see cref="NormVersion"/>.
/// </summary>
public static class XmlCanonicalizer
{
    public const int NormVersion = 1;

    private static readonly HashSet<string> VolatileAttributes = new(StringComparer.Ordinal)
    {
        "CreationDate", "ModifiedDate", "CodeModifiedDate", "InterfaceModifiedDate", "CompileDate",
        "ParameterModified", "StructureModified", "IsConsistent", "LoadMemoryLength", "WorkMemoryLength",
    };

    /// <summary>Elementi che contengono testo di commento (spazi collassati, fuori dall'hash del codice).</summary>
    internal static readonly HashSet<string> CommentElements = new(StringComparer.Ordinal)
    {
        "Comment", "LineComment", "MultilingualText",
    };

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Forma da archiviare: l'XML com'e' uscito da TIA meno DocumentInfo (che
    /// contiene la data di export e renderebbe unico ogni file). Formattazione
    /// originale conservata.
    /// </summary>
    public static string StripDocumentInfo(string xml)
    {
        XDocument doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        XElement? info = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "DocumentInfo");
        if (info == null)
        {
            return xml;
        }

        // Toglie anche lo spazio che precede l'elemento, per non lasciare righe vuote.
        if (info.PreviousNode is XText ws && string.IsNullOrWhiteSpace(ws.Value))
        {
            ws.Remove();
        }

        info.Remove();
        StringBuilder sb = new();
        using (XmlWriter w = XmlWriter.Create(sb, new XmlWriterSettings { OmitXmlDeclaration = false, Indent = false, Encoding = Encoding.UTF8 }))
        {
            doc.Save(w);
        }

        return sb.ToString();
    }

    public static XDocument Canonicalize(string xml)
    {
        XDocument source = XDocument.Parse(xml, LoadOptions.None);
        XElement root = source.Root ?? throw new XmlException("XML senza radice");

        XElement clean = Clean(root, inComment: false)!;
        XDocument doc = new(clean);

        XElement? block = BlockElement(doc);
        if (block != null)
        {
            RemoveVolatileAttributes(block);
        }

        foreach (XElement scope in UidScopes(doc))
        {
            FixWhitespaceTokens(scope);
            RenumberUids(scope);
        }

        return doc;
    }

    public static string ToText(XNode node)
    {
        StringBuilder sb = new();
        XmlWriterSettings settings = new()
        {
            OmitXmlDeclaration = true,
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
        };
        using (XmlWriter w = XmlWriter.Create(sb, settings))
        {
            node.WriteTo(w);
        }

        return sb.ToString();
    }

    /// <summary>L'elemento del blocco o dell'UDT sotto Document (SW.Blocks.FC, SW.Types.PlcStruct, ...).</summary>
    public static XElement? BlockElement(XDocument doc) =>
        doc.Root?.Name.LocalName == "Document"
            ? doc.Root.Elements().FirstOrDefault(e => e.Name.LocalName is not ("Engineering" or "DocumentInfo"))
            : doc.Root;

    private static XElement? Clean(XElement e, bool inComment)
    {
        string name = e.Name.LocalName;
        if (name is "Engineering" or "DocumentInfo" || name == "Blank")
        {
            return null;
        }

        bool comment = inComment || CommentElements.Contains(name);
        XElement copy = new(name);

        foreach (XAttribute a in e.Attributes()
                     .Where(a => !a.IsNamespaceDeclaration && a.Name.LocalName != "ID")
                     .Where(a => !(name == "NewLine" && a.Name.LocalName == "Num"))
                     .OrderBy(a => a.Name.LocalName, StringComparer.Ordinal))
        {
            copy.Add(new XAttribute(a.Name.LocalName, a.Value));
        }

        foreach (XNode node in e.Nodes())
        {
            switch (node)
            {
                case XElement child:
                    XElement? c = Clean(child, comment);
                    if (c != null)
                    {
                        copy.Add(c);
                    }

                    break;
                case XText text:
                    string value = comment ? Spaces.Replace(text.Value, " ").Trim() : text.Value;
                    if (value.Length > 0)
                    {
                        copy.Add(new XText(value));
                    }

                    break;
            }
        }

        return copy;
    }

    private static void RemoveVolatileAttributes(XElement block)
    {
        XElement? attrs = block.Element("AttributeList");
        if (attrs == null)
        {
            return;
        }

        bool autoNumber = string.Equals(attrs.Element("AutoNumber")?.Value, "true", StringComparison.OrdinalIgnoreCase);
        foreach (XElement e in attrs.Elements().ToList())
        {
            string n = e.Name.LocalName;
            if (VolatileAttributes.Contains(n) || (autoNumber && n == "Number"))
            {
                e.Remove();
            }
        }
    }

    /// <summary>Ogni rete ha il suo spazio di UId; fuori dalle reti vale il documento.</summary>
    private static IEnumerable<XElement> UidScopes(XDocument doc)
    {
        List<XElement> units = doc.Descendants().Where(IsCompileUnit).ToList();
        return units.Count > 0 ? units : new[] { doc.Root! };
    }

    internal static bool IsCompileUnit(XElement e) => e.Name.LocalName.EndsWith("CompileUnit", StringComparison.Ordinal);

    /// <summary>NewLine consecutivi (rimasti adiacenti dopo aver tolto i Blank) diventano uno.</summary>
    private static void FixWhitespaceTokens(XElement scope)
    {
        foreach (XElement nl in scope.Descendants("NewLine").ToList())
        {
            if (nl.Parent != null && nl.PreviousNode is XElement prev && prev.Name.LocalName == "NewLine")
            {
                nl.Remove();
            }
        }
    }

    private static void RenumberUids(XElement scope)
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        int next = 1;
        foreach (XElement e in scope.DescendantsAndSelf())
        {
            XAttribute? uid = e.Attribute("UId");
            if (uid == null)
            {
                continue;
            }

            if (!map.TryGetValue(uid.Value, out string? renumbered))
            {
                renumbered = next.ToString(System.Globalization.CultureInfo.InvariantCulture);
                next++;
                map[uid.Value] = renumbered;
            }

            uid.Value = renumbered;
        }
    }
}
