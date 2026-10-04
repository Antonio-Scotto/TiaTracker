using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace TiaTracker.Contracts
{
    /// <summary>
    /// Serializzatore JSON minimo per il worker net48, che non ha
    /// System.Text.Json. Nomi in camelCase, null omessi. L'app rilegge con
    /// System.Text.Json e la policy CamelCase.
    /// </summary>
    public static class MiniJson
    {
        private static readonly Dictionary<Type, PropertyInfo[]> Cache = new Dictionary<Type, PropertyInfo[]>();

        /// <summary>
        /// Legge un oggetto JSON piatto (stringhe, numeri, booleani, null) come
        /// dizionario di stringhe: basta per il file --request del worker.
        /// </summary>
        public static Dictionary<string, string> ParseFlatObject(string json)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            SkipWs(json, ref i);
            Expect(json, ref i, '{');
            SkipWs(json, ref i);
            if (i < json.Length && json[i] == '}')
            {
                return result;
            }

            while (true)
            {
                SkipWs(json, ref i);
                string key = ReadString(json, ref i);
                SkipWs(json, ref i);
                Expect(json, ref i, ':');
                SkipWs(json, ref i);
                string value;
                if (json[i] == '"')
                {
                    value = ReadString(json, ref i);
                }
                else
                {
                    int start = i;
                    while (i < json.Length && json[i] != ',' && json[i] != '}' && !char.IsWhiteSpace(json[i]))
                    {
                        i++;
                    }

                    value = json.Substring(start, i - start);
                    if (value == "null")
                    {
                        value = null;
                    }
                }

                if (value != null)
                {
                    result[key] = value;
                }

                SkipWs(json, ref i);
                if (json[i] == ',')
                {
                    i++;
                    continue;
                }

                Expect(json, ref i, '}');
                return result;
            }
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
            {
                i++;
            }
        }

        private static void Expect(string s, ref int i, char c)
        {
            if (i >= s.Length || s[i] != c)
            {
                throw new FormatException("JSON non valido: atteso '" + c + "' in posizione " + i);
            }

            i++;
        }

        private static string ReadString(string s, ref int i)
        {
            Expect(s, ref i, '"');
            StringBuilder sb = new StringBuilder();
            while (i < s.Length && s[i] != '"')
            {
                char c = s[i++];
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }

            Expect(s, ref i, '"');
            return sb.ToString();
        }

        public static string Serialize(object value, bool indented = false)
        {
            StringBuilder sb = new StringBuilder();
            Write(sb, value, indented, 0);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object value, bool indented, int depth)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            switch (value)
            {
                case string s:
                    WriteString(sb, s);
                    return;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    return;
                case int i:
                    sb.Append(i.ToString(CultureInfo.InvariantCulture));
                    return;
                case long l:
                    sb.Append(l.ToString(CultureInfo.InvariantCulture));
                    return;
                case double d:
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case DateTime dt:
                    WriteString(sb, dt.ToString("o", CultureInfo.InvariantCulture));
                    return;
                case Enum e:
                    WriteString(sb, e.ToString());
                    return;
                case IDictionary dict:
                    WriteDictionary(sb, dict, indented, depth);
                    return;
                case IEnumerable list:
                    WriteList(sb, list, indented, depth);
                    return;
            }

            WriteObject(sb, value, indented, depth);
        }

        private static void WriteObject(StringBuilder sb, object value, bool indented, int depth)
        {
            PropertyInfo[] props;
            Type type = value.GetType();
            lock (Cache)
            {
                if (!Cache.TryGetValue(type, out props))
                {
                    props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                    Cache[type] = props;
                }
            }

            sb.Append('{');
            bool first = true;
            foreach (PropertyInfo p in props)
            {
                if (p.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                object v = p.GetValue(value, null);
                if (v == null)
                {
                    continue;
                }

                Separator(sb, ref first, indented, depth + 1);
                WriteString(sb, CamelCase(p.Name));
                sb.Append(indented ? ": " : ":");
                Write(sb, v, indented, depth + 1);
            }

            Close(sb, first, indented, depth);
            sb.Append('}');
        }

        private static void WriteDictionary(StringBuilder sb, IDictionary dict, bool indented, int depth)
        {
            sb.Append('{');
            bool first = true;
            foreach (DictionaryEntry entry in dict)
            {
                if (entry.Value == null)
                {
                    continue;
                }

                Separator(sb, ref first, indented, depth + 1);
                WriteString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                sb.Append(indented ? ": " : ":");
                Write(sb, entry.Value, indented, depth + 1);
            }

            Close(sb, first, indented, depth);
            sb.Append('}');
        }

        private static void WriteList(StringBuilder sb, IEnumerable list, bool indented, int depth)
        {
            sb.Append('[');
            bool first = true;
            foreach (object item in list)
            {
                Separator(sb, ref first, indented, depth + 1);
                Write(sb, item, indented, depth + 1);
            }

            Close(sb, first, indented, depth);
            sb.Append(']');
        }

        private static void Separator(StringBuilder sb, ref bool first, bool indented, int depth)
        {
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            if (indented)
            {
                sb.Append('\n').Append(' ', depth * 2);
            }
        }

        private static void Close(StringBuilder sb, bool empty, bool indented, int depth)
        {
            if (indented && !empty)
            {
                sb.Append('\n').Append(' ', depth * 2);
            }
        }

        private static string CamelCase(string name)
        {
            if (string.IsNullOrEmpty(name) || char.IsLower(name[0]))
            {
                return name;
            }

            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            sb.Append('"');
        }
    }
}
