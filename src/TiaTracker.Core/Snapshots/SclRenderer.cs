using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace TiaTracker.Core.Snapshots;

/// <summary>
/// Sorgente SCL "come in TIA" ricostruito dall'XML SimaticML di un blocco o di un UDT:
/// intestazione, interfaccia (VAR_INPUT, VAR_OUTPUT, ...) e codice. Serve per vedere i
/// blocchi che TIA non esporta come sorgente: DB di istanza, blocchi LAD/FBD (anche F),
/// reti SCL dentro blocchi LAD. Le reti LAD/FBD diventano istruzioni SCL equivalenti
/// (contatti in AND, rami in OR, bobine come assegnazioni, box come chiamate): e' una
/// lettura, non un sorgente da reimportare.
/// </summary>
public static class SclRenderer
{
    private const string Indent = "   ";

    /// <summary>Blocco F in corso: nei sorgenti dei blocchi F TIA non scrive S7_SetPoint.</summary>
    [ThreadStatic]
    private static bool _safety;

    /// <summary>Null se l'XML non e' leggibile o non contiene un blocco o un tipo.</summary>
    public static string? FromXml(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            return null;
        }

        XElement? block = XmlCanonicalizer.BlockElement(doc);
        if (block == null)
        {
            return null;
        }

        string kind = block.Name.LocalName;
        _safety = Attr(Child(block, "AttributeList"), "ProgrammingLanguage")?.StartsWith("F_", StringComparison.Ordinal) == true;
        return kind switch
        {
            "SW.Types.PlcStruct" => Udt(block),
            "SW.Blocks.GlobalDB" or "SW.Blocks.InstanceDB" or "SW.Blocks.ArrayDB" => DataBlock(block, kind),
            "SW.Blocks.FB" or "SW.Blocks.FC" or "SW.Blocks.OB" => CodeBlock(block, kind),
            _ => null,
        };
    }

    // ---------- blocchi ----------

    private static string CodeBlock(XElement block, string kind)
    {
        XElement attrs = Child(block, "AttributeList") ?? new XElement("AttributeList");
        string name = Attr(attrs, "Name") ?? "?";
        List<XElement> sections = Sections(attrs);
        StringBuilder sb = new();

        string keyword = kind switch
        {
            "SW.Blocks.FB" => "FUNCTION_BLOCK",
            "SW.Blocks.FC" => "FUNCTION",
            _ => "ORGANIZATION_BLOCK",
        };
        sb.Append(keyword).Append(' ').Append(Quote(name));
        if (kind == "SW.Blocks.FC")
        {
            XElement? ret = sections.FirstOrDefault(s => s.Attribute("Name")?.Value == "Return")?.Elements().FirstOrDefault(e => e.Name.LocalName == "Member");
            sb.Append(" : ").Append(ret?.Attribute("Datatype")?.Value ?? "Void");
        }

        sb.Append('\n');
        string? title = Text(block, "Title");
        if (!string.IsNullOrWhiteSpace(title))
        {
            sb.Append("TITLE = ").Append(title.Trim()).Append('\n');
        }

        Header(sb, attrs);
        Comment(sb, Text(block, "Comment"), "");

        foreach (XElement section in sections)
        {
            string? keywordOf = (section.Attribute("Name")?.Value, kind) switch
            {
                ("Input", _) => "VAR_INPUT",
                ("Output", _) => "VAR_OUTPUT",
                ("InOut", _) => "VAR_IN_OUT",
                ("Static", _) => "VAR",
                ("Temp", _) => "VAR_TEMP",
                ("Constant", _) => "VAR CONSTANT",
                _ => null,
            };
            if (keywordOf != null)
            {
                Section(sb, section, keywordOf, retainable: kind == "SW.Blocks.FB" && keywordOf is "VAR" or "VAR_INPUT" or "VAR_OUTPUT" or "VAR_IN_OUT");
            }
        }

        sb.Append("\nBEGIN\n");
        Code(sb, block);
        sb.Append("END_").Append(keyword).Append('\n');
        return sb.ToString();
    }

    private static string DataBlock(XElement block, string kind)
    {
        XElement attrs = Child(block, "AttributeList") ?? new XElement("AttributeList");
        string name = Attr(attrs, "Name") ?? "?";
        List<XElement> sections = Sections(attrs);
        StringBuilder sb = new();
        sb.Append("DATA_BLOCK ").Append(Quote(name)).Append('\n');
        string? title = Text(block, "Title");
        if (!string.IsNullOrWhiteSpace(title))
        {
            sb.Append("TITLE = ").Append(title.Trim()).Append('\n');
        }

        Header(sb, attrs);
        bool anyRetain = sections.SelectMany(s => s.Elements()).Any(m => m.Attribute("Remanence")?.Value == "Retain");
        if (!anyRetain)
        {
            sb.Append("NON_RETAIN\n");
        }

        Comment(sb, Text(block, "Comment"), "");
        string? of = Attr(attrs, "InstanceOfName");
        if (of != null)
        {
            // In TIA un DB di istanza ha solo il riferimento al tipo: l'interfaccia (con i valori dell'istanza) e' qui per leggerla.
            sb.Append(Quote(of)).Append('\n');
            sb.Append("// Interfaccia di ").Append(Quote(of)).Append(" con i valori di avvio dell'istanza\n");
            foreach (XElement section in sections)
            {
                string? keyword = section.Attribute("Name")?.Value switch
                {
                    "Input" => "VAR_INPUT",
                    "Output" => "VAR_OUTPUT",
                    "InOut" => "VAR_IN_OUT",
                    "Static" => "VAR",
                    _ => null,
                };
                if (keyword != null)
                {
                    Section(sb, section, keyword, retainable: anyRetain);
                }
            }
        }
        else if (Attr(attrs, "MemoryLayout") == "Standard")
        {
            // DB non ottimizzato: TIA lo scrive come STRUCT.
            sb.Append(Indent).Append("STRUCT \n");
            foreach (XElement m in sections.SelectMany(s => s.Elements()).Where(e => e.Name.LocalName == "Member"))
            {
                Member(sb, m, Indent + Indent);
            }

            sb.Append(Indent).Append("END_STRUCT;\n\n");
        }
        else
        {
            foreach (XElement section in sections.Where(s => s.Attribute("Name")?.Value is "Static" or null))
            {
                Section(sb, section, "VAR", retainable: anyRetain);
            }
        }

        sb.Append("\nBEGIN\n\nEND_DATA_BLOCK\n");
        return sb.ToString();
    }

    private static string Udt(XElement block)
    {
        XElement attrs = Child(block, "AttributeList") ?? new XElement("AttributeList");
        StringBuilder sb = new();
        sb.Append("TYPE ").Append(Quote(Attr(attrs, "Name") ?? "?")).Append('\n');
        string? title = Text(block, "Title");
        if (!string.IsNullOrWhiteSpace(title))
        {
            sb.Append("TITLE = ").Append(title.Trim()).Append('\n');
        }

        Header(sb, attrs);
        Comment(sb, Text(block, "Comment"), "");
        sb.Append(Indent).Append("STRUCT\n");
        foreach (XElement section in Sections(attrs))
        {
            foreach (XElement m in section.Elements().Where(e => e.Name.LocalName == "Member"))
            {
                Member(sb, m, Indent + Indent);
            }
        }

        sb.Append(Indent).Append("END_STRUCT;\n\nEND_TYPE\n");
        return sb.ToString();
    }

    /// <summary>{ S7_Optimized_Access }, AUTHOR, FAMILY, NAME, VERSION come nei sorgenti generati da TIA.</summary>
    private static void Header(StringBuilder sb, XElement attrs)
    {
        // Gli UDT non ce l'hanno; i blocchi F non esportano MemoryLayout ma sono ottimizzati.
        string? layout = Attr(attrs, "MemoryLayout");
        if (layout != null || Attr(attrs, "ProgrammingLanguage")?.StartsWith("F_", StringComparison.Ordinal) == true)
        {
            sb.Append("{ S7_Optimized_Access := '").Append(layout is null or "Optimized" ? "TRUE" : "FALSE").Append("' }\n");
        }

        foreach ((string attr, string key) in new[] { ("HeaderAuthor", "AUTHOR"), ("HeaderFamily", "FAMILY"), ("HeaderName", "NAME") })
        {
            string? v = Attr(attrs, attr);
            if (!string.IsNullOrWhiteSpace(v))
            {
                sb.Append(key).Append(" : ").Append(v).Append('\n');
            }
        }

        sb.Append("VERSION : ").Append(Attr(attrs, "HeaderVersion") ?? "0.1").Append('\n');
    }

    private static void Comment(StringBuilder sb, string? text, string indent)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (string line in text.Replace("\r", "").TrimEnd().Split('\n'))
        {
            sb.Append(indent).Append("//").Append(line).Append('\n');
        }
    }

    // ---------- interfaccia ----------

    private static List<XElement> Sections(XElement attrs) =>
        Child(attrs, "Interface")?.Elements().FirstOrDefault(e => e.Name.LocalName == "Sections")?.Elements()
            .Where(e => e.Name.LocalName == "Section").ToList() ?? new List<XElement>();

    /// <summary>Una sezione; i membri ritentivi consecutivi vanno in "VAR RETAIN" come nei sorgenti di TIA.</summary>
    private static void Section(StringBuilder sb, XElement section, string keyword, bool retainable)
    {
        // Informative: membri che TIA mostra ma non scrive (ingressi degli OB).
        List<XElement> members = section.Elements()
            .Where(e => e.Name.LocalName == "Member" && e.Attribute("Informative")?.Value != "true").ToList();
        int i = 0;
        while (i < members.Count)
        {
            bool retain = retainable && members[i].Attribute("Remanence")?.Value == "Retain";
            sb.Append(Indent).Append(keyword).Append(retain ? " RETAIN" : " ").Append('\n');
            while (i < members.Count && (retainable && members[i].Attribute("Remanence")?.Value == "Retain") == retain)
            {
                Member(sb, members[i], Indent + Indent);
                i++;
            }

            sb.Append(Indent).Append("END_VAR\n");
        }

        // Una riga vuota fra le sezioni (non fra i gruppi RETAIN della stessa); una sezione vuota non si scrive.
        if (members.Count > 0)
        {
            sb.Append('\n');
        }
    }

    private static void Member(StringBuilder sb, XElement m, string indent)
    {
        string name = m.Attribute("Name")?.Value ?? "?";
        string type = m.Attribute("Datatype")?.Value ?? "?";
        sb.Append(indent).Append(Identifier(name));

        List<string> attrs = new();
        string? version = m.Attribute("Version")?.Value;
        if (version != null)
        {
            attrs.Add("InstructionName := '" + type + "'");
            attrs.Add("LibVersion := '" + version + "'");
        }

        Dictionary<string, string> flags = (Child(m, "AttributeList")?.Elements() ?? Enumerable.Empty<XElement>())
            .Where(a => a.Name.LocalName == "BooleanAttribute")
            .GroupBy(a => a.Attribute("Name")?.Value ?? "")
            .ToDictionary(g => g.Key, g => g.First().Value.Trim());
        foreach (string f in new[] { "ExternalAccessible", "ExternalVisible", "ExternalWritable" })
        {
            if (flags.GetValueOrDefault(f) == "false")
            {
                attrs.Add(f + " := 'False'");
            }
        }

        // S7_SetPoint si scrive solo se diverso dal default: vero per UDT e istanze (anche in array), falso per gli altri.
        if (!_safety && flags.TryGetValue("SetPoint", out string? setPoint))
        {
            bool structured = type.EndsWith('"') || (version != null && type != "DTL");
            if ((setPoint == "true") != structured)
            {
                attrs.Add("S7_SetPoint := '" + (setPoint == "true" ? "True" : "False") + "'");
            }
        }

        if (attrs.Count > 0)
        {
            sb.Append(" {").Append(version != null ? "" : " ").Append(string.Join("; ", attrs)).Append('}');
        }

        sb.Append(" : ").Append(type);
        string? start = StartValue(m, type);
        if (start != null)
        {
            sb.Append(" := ").Append(start);
        }

        // Struct (anche Array of Struct): i membri dentro. Tipi e istanze: solo il nome del tipo.
        List<XElement> children = m.Elements().Where(e => e.Name.LocalName == "Member").ToList();
        bool isStruct = type.EndsWith("Struct", StringComparison.OrdinalIgnoreCase);
        string? comment = Text(m, null);
        if (isStruct)
        {
            AppendComment(sb, comment);
            sb.Append('\n');
            foreach (XElement c in children)
            {
                Member(sb, c, indent + Indent);
            }

            sb.Append(indent).Append("END_STRUCT;\n");
            return;
        }

        sb.Append(';');
        AppendComment(sb, comment);
        sb.Append('\n');
    }

    private static void AppendComment(StringBuilder sb, string? comment)
    {
        if (!string.IsNullOrWhiteSpace(comment))
        {
            sb.Append("   // ").Append(comment.Replace("\r", "").Replace('\n', ' ').Trim());
        }
    }

    /// <summary>Valore di avvio; per gli array di tipi semplici la lista [v0, v1, ...] dai Subelement.</summary>
    private static string? StartValue(XElement m, string type)
    {
        string? direct = Child(m, "StartValue")?.Value;
        if (direct != null)
        {
            return direct;
        }

        List<(int Index, string Value)> items = new();
        foreach (XElement sub in m.Elements().Where(e => e.Name.LocalName == "Subelement"))
        {
            string? v = Child(sub, "StartValue")?.Value;
            if (v == null || !int.TryParse(sub.Attribute("Path")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            {
                continue;
            }

            items.Add((index, v));
        }

        if (items.Count == 0 || !type.StartsWith("Array", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return "[" + string.Join(", ", items.OrderBy(x => x.Index).Select(x => x.Value)) + "]";
    }

    // ---------- codice ----------

    private static void Code(StringBuilder sb, XElement block)
    {
        List<XElement> units = Child(block, "ObjectList")?.Elements().Where(e => e.Name.LocalName == "SW.Blocks.CompileUnit").ToList()
                               ?? new List<XElement>();
        bool single = units.Count == 1;
        int n = 0;
        foreach (XElement unit in units)
        {
            n++;
            XElement? attrs = Child(unit, "AttributeList");
            XElement? source = Child(attrs, "NetworkSource")?.Elements().FirstOrDefault();
            string language = Attr(attrs, "ProgrammingLanguage") ?? "";
            StringBuilder code = new();
            if (source?.Name.LocalName == "StructuredText")
            {
                StructuredText(code, source);
            }
            else if (source?.Name.LocalName == "FlgNet")
            {
                new FlgNetWriter(source).Write(code);
            }

            if (!single)
            {
                string? title = Text(unit, "Title");
                sb.Append("\t// ---- Rete ").Append(n.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrWhiteSpace(title))
                {
                    sb.Append(": ").Append(title.Trim());
                }

                if (language.Length > 0 && language != "SCL")
                {
                    sb.Append(" (").Append(language).Append(", tradotta in SCL)");
                }

                sb.Append(" ----\n");
                Comment(sb, Text(unit, "Comment"), "\t");
            }

            string text = code.ToString().Replace("\r", "").Trim('\n').TrimEnd(' ', '\t');
            if (text.Length == 0 && !single)
            {
                text = "// rete vuota";
            }

            foreach (string line in text.Split('\n'))
            {
                sb.Append(line.Length > 0 ? "\t" + line : "").Append('\n');
            }

            if (!single && n < units.Count)
            {
                sb.Append('\n');
            }
        }
    }

    /// <summary>I token della rete SCL nell'ordine dell'XML: e' gia' il testo scritto in TIA.</summary>
    private static void StructuredText(StringBuilder sb, XElement e)
    {
        foreach (XElement c in e.Elements())
        {
            Token(sb, c);
        }
    }

    private static void Token(StringBuilder sb, XElement e)
    {
        switch (e.Name.LocalName)
        {
            case "Token":
                sb.Append(e.Attribute("Text")?.Value);
                break;
            case "Blank":
                sb.Append(' ', Num(e));
                break;
            case "NewLine":
                sb.Append('\n', Num(e));
                break;
            case "Text":
                sb.Append(e.Value);
                break;
            case "LineComment":
                sb.Append(e.Attribute("Inserted")?.Value == "true" ? "(*" : "//");
                StructuredText(sb, e);
                if (e.Attribute("Inserted")?.Value == "true" && e.Attribute("NoClosingBracket")?.Value != "true")
                {
                    sb.Append("*)");
                }

                break;
            case "Comment":
                sb.Append("(*");
                foreach (XElement t in e.Descendants().Where(d => d.Name.LocalName is "Text" or "MultiLanguageText"))
                {
                    sb.Append(t.Value);
                    break;
                }

                sb.Append("*)");
                break;
            case "Access":
                Access(sb, e);
                break;
            case "Symbol":
                StructuredText(sb, e);
                break;
            case "Component":
                Component(sb, e, first: false, scope: null);
                break;
            case "Parameter":
                sb.Append(e.Attribute("Name")?.Value);
                StructuredText(sb, e);
                break;
            case "Instruction":
                // Istruzioni generiche: il tipo scelto fa parte del nome (CONCAT su String -> CONCAT_STRING).
                sb.Append(e.Attribute("Name")?.Value);
                foreach (XElement t in e.Elements().Where(x => x.Name.LocalName == "TemplateValue" && x.Attribute("Type")?.Value == "Type"))
                {
                    sb.Append('_').Append(t.Value.ToUpperInvariant());
                }

                StructuredText(sb, e);
                break;
            case "CallInfo":
                StructuredText(sb, e);
                break;
            case "Instance":
                Symbol(sb, e, e.Attribute("Scope")?.Value);
                break;
            case "Constant":
                // Costanti con nome (globali e locali) o letterali.
                if (e.Attribute("Name") is XAttribute named)
                {
                    sb.Append(named.Value);
                }
                else
                {
                    sb.Append(Child(e, "ConstantValue")?.Value);
                }

                break;
            case "BooleanAttribute":
            case "TemplateValue":
                break;
            default:
                StructuredText(sb, e);
                break;
        }
    }

    private static void Access(StringBuilder sb, XElement e)
    {
        string? scope = e.Attribute("Scope")?.Value;
        foreach (XElement c in e.Elements())
        {
            switch (c.Name.LocalName)
            {
                case "Symbol":
                    Symbol(sb, c, scope);
                    break;
                case "Constant":
                    string? constName = c.Attribute("Name")?.Value;
                    if (constName != null)
                    {
                        sb.Append(scope == "LocalConstant" ? "#" + constName : Quote(constName));
                    }
                    else
                    {
                        sb.Append(Child(c, "ConstantValue")?.Value);
                    }

                    break;
                default:
                    Token(sb, c);
                    break;
            }
        }
    }

    /// <summary>
    /// Simbolo: componenti e separatori come nell'XML. Il primo componente prende # se locale,
    /// le virgolette se globale; gli altri le virgolette se TIA le ha scritte (HasQuotes).
    /// </summary>
    private static void Symbol(StringBuilder sb, XElement symbol, string? scope)
    {
        bool first = true;
        foreach (XElement c in symbol.Elements())
        {
            if (c.Name.LocalName == "Component")
            {
                Component(sb, c, first, scope);
                first = false;
            }
            else
            {
                Token(sb, c);
            }
        }
    }

    private static void Component(StringBuilder sb, XElement c, bool first, string? scope)
    {
        string name = c.Attribute("Name")?.Value ?? "";
        bool quotes = c.Elements().Any(a => a.Name.LocalName == "BooleanAttribute" && a.Attribute("Name")?.Value == "HasQuotes" && a.Value == "true");
        bool hash = c.Elements().Any(a => a.Name.LocalName == "BooleanAttribute" && a.Attribute("Name")?.Value == "HasHash" && a.Value == "true");
        if (first && scope is "LocalVariable" or "LocalConstant")
        {
            sb.Append('#');
        }
        else if (hash)
        {
            sb.Append('#');
        }

        sb.Append(quotes || (first && scope is "GlobalVariable" or "GlobalConstant") ? "\"" + name + "\"" : name);
        foreach (XElement child in c.Elements().Where(x => x.Name.LocalName != "BooleanAttribute"))
        {
            Token(sb, child);
        }
    }

    private static int Num(XElement e) =>
        int.TryParse(e.Attribute("Num")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : 1;

    // ---------- LAD / FBD ----------

    /// <summary>
    /// Una rete LAD/FBD come istruzioni SCL. Ogni Wire ha una sorgente (il primo figlio:
    /// barra di alimentazione, operando o uscita di un elemento) e una o piu' destinazioni.
    /// Il valore di un ingresso e' l'espressione della sua sorgente; contatti in serie
    /// diventano AND, il box O un OR, le bobine assegnazioni, gli altri box chiamate.
    /// </summary>
    private sealed class FlgNetWriter
    {
        private readonly Dictionary<string, XElement> _parts = new();
        private readonly List<XElement> _order = new();

        /// <summary>(UId elemento, pin di ingresso) -> sorgente.</summary>
        private readonly Dictionary<(string, string), XElement> _inputs = new();

        /// <summary>(UId elemento, pin di uscita) -> operandi collegati.</summary>
        private readonly Dictionary<(string, string), List<string>> _outputs = new();

        private readonly Dictionary<(string, string), Expr> _memo = new();

        public FlgNetWriter(XElement net)
        {
            foreach (XElement p in Child(net, "Parts")?.Elements() ?? Enumerable.Empty<XElement>())
            {
                string? uid = p.Attribute("UId")?.Value;
                if (uid != null)
                {
                    _parts[uid] = p;
                    if (p.Name.LocalName != "Access")
                    {
                        _order.Add(p);
                    }
                }
            }

            foreach (XElement w in Child(net, "Wires")?.Elements() ?? Enumerable.Empty<XElement>())
            {
                List<XElement> ends = w.Elements().ToList();
                if (ends.Count < 2)
                {
                    continue;
                }

                // OpenCon: pin lasciato aperto, non c'e' niente da scrivere.
                XElement source = ends[0];
                if (source.Name.LocalName == "OpenCon")
                {
                    continue;
                }

                foreach (XElement sink in ends.Skip(1))
                {
                    if (sink.Name.LocalName == "NameCon")
                    {
                        _inputs[(Uid(sink), Pin(sink))] = source;
                    }
                    else if (sink.Name.LocalName == "IdentCon" && source.Name.LocalName == "NameCon")
                    {
                        (string, string) key = (Uid(source), Pin(source));
                        if (!_outputs.TryGetValue(key, out List<string>? list))
                        {
                            _outputs[key] = list = new List<string>();
                        }

                        list.Add(Operand(Uid(sink)));
                    }
                }
            }
        }

        public void Write(StringBuilder sb)
        {
            foreach (XElement p in _order)
            {
                string uid = Uid(p);
                string name = PartName(p);
                switch (name)
                {
                    case "Contact" or "PContact" or "NContact" or "O" or "A" or "X" or "Eq" or "Ne" or "Gt" or "Lt" or "Ge" or "Le":
                        break;
                    case "Coil" or "Assignment":
                    {
                        Expr value = Input(uid, "in");
                        string target = Input(uid, "operand").Text;
                        sb.Append(target).Append(" := ").Append(Negated(p, "operand") ? Not(value).Text : value.Text).Append(";\n");
                        break;
                    }
                    case "SCoil" or "RCoil" or "Sr" or "Rs":
                    {
                        string target = Input(uid, "operand").Text;
                        bool set = name == "SCoil";
                        if (name is "Sr" or "Rs")
                        {
                            sb.Append("// ").Append(name).Append(" su ").Append(target).Append(": S = ").Append(Input(uid, "s").Text)
                                .Append(", R = ").Append(Input(uid, "r1").Text).Append('\n');
                            break;
                        }

                        sb.Append("IF ").Append(Input(uid, "in").Text).Append(" THEN\n")
                            .Append(Indent).Append(target).Append(" := ").Append(set ? "TRUE" : "FALSE").Append(";\n")
                            .Append("END_IF;\n");
                        break;
                    }
                    case "Move":
                    {
                        string body = string.Join("", Outputs(uid, "out1").Select(t => t + " := " + Input(uid, "in").Text + ";\n"));
                        Guarded(sb, uid, body);
                        break;
                    }
                    default:
                        Guarded(sb, uid, CallText(p) + ";\n");
                        break;
                }
            }
        }

        /// <summary>Un box con EN collegato a qualcosa che non e' la barra: dentro IF.</summary>
        private void Guarded(StringBuilder sb, string uid, string body)
        {
            Expr en = Input(uid, "en");
            if (en.IsTrue)
            {
                sb.Append(body);
                return;
            }

            sb.Append("IF ").Append(en.Text).Append(" THEN\n");
            foreach (string line in body.TrimEnd('\n').Split('\n'))
            {
                sb.Append(Indent).Append(line).Append('\n');
            }

            sb.Append("END_IF;\n");
        }

        private string CallText(XElement p)
        {
            string uid = Uid(p);
            string target = Callee(p);
            List<string> args = new();
            foreach (((string u, string pin), XElement _) in _inputs.Where(kv => kv.Key.Item1 == uid && kv.Key.Item2 != "en"))
            {
                args.Add(pin + " := " + Input(u, pin).Text);
            }

            foreach (((string u, string pin), List<string> targets) in _outputs.Where(kv => kv.Key.Item1 == uid && kv.Key.Item2 != "eno"))
            {
                args.AddRange(targets.Select(t => pin + " => " + t));
            }

            return target + "(" + string.Join(", ", args) + ")";
        }

        /// <summary>Istanza (#locale o "DB") se c'e', altrimenti il nome del blocco o dell'istruzione.</summary>
        private static string Callee(XElement p)
        {
            XElement? info = p.Name.LocalName == "Call" ? Child(p, "CallInfo") : p;
            XElement? instance = Child(info, "Instance");
            if (instance != null)
            {
                StringBuilder sb = new();
                Symbol(sb, instance, instance.Attribute("Scope")?.Value);
                return sb.ToString();
            }

            string name = info?.Attribute("Name")?.Value ?? "?";
            return p.Name.LocalName == "Call" ? Quote(name) : name;
        }

        private Expr Input(string uid, string pin)
        {
            if (_memo.TryGetValue((uid, pin), out Expr? done))
            {
                return done;
            }

            Expr result = Expr.True;
            _memo[(uid, pin)] = result;
            if (_inputs.TryGetValue((uid, pin), out XElement? source))
            {
                result = Source(source);
                if (_parts.TryGetValue(uid, out XElement? part) && Negated(part, pin) && PartName(part) is not ("Contact" or "Coil"))
                {
                    result = Not(result);
                }
            }

            _memo[(uid, pin)] = result;
            return result;
        }

        private Expr Source(XElement source) => source.Name.LocalName switch
        {
            "Powerrail" => Expr.True,
            "IdentCon" => Expr.Atom(Operand(Uid(source))),
            "NameCon" => Output(Uid(source), Pin(source)),
            _ => Expr.True,
        };

        /// <summary>Valore dell'uscita di un elemento.</summary>
        private Expr Output(string uid, string pin)
        {
            if (!_parts.TryGetValue(uid, out XElement? p))
            {
                return Expr.Atom("?");
            }

            string name = PartName(p);
            switch (name)
            {
                case "Contact":
                {
                    Expr op = Input(uid, "operand");
                    return And(Input(uid, "in"), Negated(p, "operand") ? Not(op) : op);
                }
                case "PContact" or "NContact":
                    return And(Input(uid, "in"), Expr.Atom((name == "PContact" ? "R_TRIG(" : "F_TRIG(") + Input(uid, "operand").Text + ")"));
                case "Coil" or "SCoil" or "RCoil":
                    return Input(uid, "in");
                case "O" or "A" or "X":
                {
                    List<Expr> ins = InputPins(uid).Select(pinName => Input(uid, pinName)).ToList();
                    return ins.Count == 0 ? Expr.True : ins.Aggregate((a, b) => name == "O" ? Or(a, b) : name == "A" ? And(a, b) : Xor(a, b));
                }
                case "Eq" or "Ne" or "Gt" or "Lt" or "Ge" or "Le":
                {
                    string op = name switch { "Eq" => "=", "Ne" => "<>", "Gt" => ">", "Lt" => "<", "Ge" => ">=", _ => "<=" };
                    Expr cmp = Expr.Atom("(" + Input(uid, "in1").Text + " " + op + " " + Input(uid, "in2").Text + ")");
                    return And(Input(uid, "pre"), cmp);
                }
                default:
                    // Uscita di un box: ENO o un parametro dell'istanza.
                    if (pin == "eno")
                    {
                        return Input(uid, "en");
                    }

                    return Expr.Atom(Callee(p) + "." + pin);
            }
        }

        private IEnumerable<string> InputPins(string uid) =>
            _inputs.Keys.Where(k => k.Item1 == uid && k.Item2.StartsWith("in", StringComparison.Ordinal))
                .Select(k => k.Item2).OrderBy(x => x.Length).ThenBy(x => x, StringComparer.Ordinal);

        private IEnumerable<string> Outputs(string uid, string pin) =>
            _outputs.TryGetValue((uid, pin), out List<string>? list) ? list : Enumerable.Empty<string>();

        private string Operand(string uid)
        {
            if (!_parts.TryGetValue(uid, out XElement? access))
            {
                return "?";
            }

            StringBuilder sb = new();
            string? scope = access.Attribute("Scope")?.Value;
            XElement? symbol = Child(access, "Symbol");
            XElement? constant = Child(access, "Constant");
            if (symbol != null)
            {
                bool first = true;
                foreach (XElement c in symbol.Elements().Where(x => x.Name.LocalName == "Component"))
                {
                    if (!first)
                    {
                        sb.Append('.');
                    }

                    FlgComponent(sb, c, first, scope);
                    first = false;
                }
            }
            else if (constant != null)
            {
                string? named = constant.Attribute("Name")?.Value;
                sb.Append(named != null
                    ? scope == "LocalConstant" ? "#" + named : Quote(named)
                    : Child(constant, "ConstantValue")?.Value ?? "?");
            }

            return sb.ToString();
        }

        /// <summary>Componente LAD: niente token, indici come Access figli.</summary>
        private void FlgComponent(StringBuilder sb, XElement c, bool first, string? scope)
        {
            string name = c.Attribute("Name")?.Value ?? "";
            sb.Append(first && scope is "LocalVariable" ? "#" + name
                : first && scope is "GlobalVariable" ? Quote(name)
                : Identifier(name));
            List<XElement> indexes = c.Elements().Where(x => x.Name.LocalName == "Access").ToList();
            if (indexes.Count > 0)
            {
                sb.Append('[').Append(string.Join(", ", indexes.Select(IndexText))).Append(']');
            }
        }

        private string IndexText(XElement access)
        {
            string? uid = access.Attribute("UId")?.Value;
            if (uid != null && !_parts.ContainsKey(uid))
            {
                _parts[uid] = access;
            }

            return uid != null ? Operand(uid) : Child(Child(access, "Constant"), "ConstantValue")?.Value ?? "?";
        }

        private static bool Negated(XElement part, string pin) =>
            part.Elements().Any(e => e.Name.LocalName == "Negated" && e.Attribute("Name")?.Value == pin);

        private static string PartName(XElement p) => p.Name.LocalName == "Call" ? "Call" : p.Attribute("Name")?.Value ?? "";

        private static string Uid(XElement e) => e.Attribute("UId")?.Value ?? "";

        private static string Pin(XElement e) => e.Attribute("Name")?.Value ?? "";
    }

    /// <summary>Espressione booleana con la sua precedenza, per mettere le parentesi solo dove servono.</summary>
    private sealed record Expr(string Text, int Precedence)
    {
        public const int OrLevel = 1;
        public const int XorLevel = 2;
        public const int AndLevel = 3;
        public const int AtomLevel = 4;

        public static readonly Expr True = new("TRUE", AtomLevel);

        public bool IsTrue => ReferenceEquals(this, True) || Text == "TRUE";

        public static Expr Atom(string text) => new(text, AtomLevel);

        public string Wrapped(int level) => Precedence < level ? "(" + Text + ")" : Text;
    }

    private static Expr And(Expr a, Expr b) =>
        a.IsTrue ? b : b.IsTrue ? a : new Expr(a.Wrapped(Expr.AndLevel) + " AND " + b.Wrapped(Expr.AndLevel), Expr.AndLevel);

    private static Expr Or(Expr a, Expr b) =>
        a.IsTrue || b.IsTrue ? Expr.True : new Expr(a.Wrapped(Expr.OrLevel) + " OR " + b.Wrapped(Expr.OrLevel), Expr.OrLevel);

    private static Expr Xor(Expr a, Expr b) => new(a.Wrapped(Expr.XorLevel) + " XOR " + b.Wrapped(Expr.XorLevel), Expr.XorLevel);

    private static Expr Not(Expr a) => Expr.Atom("NOT " + a.Wrapped(Expr.AtomLevel));

    // ---------- helper ----------

    private static XElement? Child(XElement? e, string localName) =>
        e?.Elements().FirstOrDefault(x => x.Name.LocalName == localName);

    private static string? Attr(XElement? attrs, string name) => Child(attrs, name)?.Value;

    /// <summary>
    /// Testo multilingua: il Title/Comment del blocco o della rete (MultilingualText con
    /// CompositionName), oppure, con <paramref name="composition"/> null, il Comment di un
    /// membro dell'interfaccia. Prima lingua non vuota.
    /// </summary>
    private static string? Text(XElement e, string? composition)
    {
        if (composition == null)
        {
            return Child(e, "Comment")?.Elements().Select(t => t.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        }

        XElement? text = Child(e, "ObjectList")?.Elements()
            .FirstOrDefault(x => x.Name.LocalName == "MultilingualText" && x.Attribute("CompositionName")?.Value == composition);
        return text?.Descendants().Where(x => x.Name.LocalName == "Text").Select(x => x.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    private static string Quote(string name) => name.StartsWith('"') ? name : "\"" + name + "\"";

    /// <summary>Parole che in SCL vanno tra virgolette anche se sono identificatori validi.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "NAME", "TITLE", "VERSION", "AUTHOR", "FAMILY", "BEGIN", "END", "VAR", "STRUCT", "TYPE", "ARRAY", "OF",
        "IF", "THEN", "ELSE", "ELSIF", "CASE", "FOR", "TO", "BY", "DO", "WHILE", "REPEAT", "UNTIL", "RETURN", "EXIT", "CONTINUE",
        "AND", "OR", "XOR", "NOT", "MOD", "TRUE", "FALSE", "REGION", "GOTO",
        "BOOL", "BYTE", "WORD", "DWORD", "LWORD", "SINT", "INT", "DINT", "LINT", "USINT", "UINT", "UDINT", "ULINT",
        "REAL", "LREAL", "TIME", "LTIME", "DATE", "TOD", "LTOD", "DT", "LDT", "DTL", "CHAR", "WCHAR", "STRING", "WSTRING", "VARIANT", "VOID",
    };

    /// <summary>Nome di variabile: tra virgolette se non e' un identificatore semplice o e' una parola riservata.</summary>
    private static string Identifier(string name)
    {
        if (name.Length == 0 || name.StartsWith('"'))
        {
            return name;
        }

        bool plain = (char.IsLetter(name[0]) || name[0] == '_') && name.All(ch => char.IsLetterOrDigit(ch) || ch == '_') && !Reserved.Contains(name);
        return plain ? name : "\"" + name + "\"";
    }
}
