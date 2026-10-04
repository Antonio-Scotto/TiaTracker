using System;
using System.Collections.Generic;
using System.Globalization;
using Siemens.Engineering;

namespace TiaTracker.Worker
{
    /// <summary>
    /// Lettura attributi in blocco. Chiedere un attributo assente e assorbire
    /// l'eccezione costa un giro COM; anche GetAttributeInfos() per blocco
    /// costa ~50 giri. L'elenco degli attributi dipende dalla classe, non
    /// dall'istanza: si paga una volta per OB, FB, FC, GlobalDB, ..., poi una
    /// sola GetAttributes per oggetto (dal toolkit: >10 min -> <1 min su 565 blocchi).
    /// </summary>
    internal sealed class AttributeReader
    {
        internal static readonly string[] BlockAttributes =
        {
            "ProgrammingLanguage", "Number", "AutoNumber", "InstanceOfName", "InstanceOfType", "MemoryLayout",
            "IsKnowHowProtected", "IsConsistent", "IsWriteProtected", "HeaderAuthor", "HeaderFamily", "HeaderName",
            "HeaderVersion", "CreationDate", "CodeModifiedDate", "InterfaceModifiedDate", "ModifiedDate", "CompileDate",
            "ParameterModified", "StructureModified", "IsIECCheckEnabled", "IsSystemBlock", "IsFailsafe",
        };

        internal static readonly string[] TypeAttributes =
        {
            "CreationDate", "ModifiedDate", "InterfaceModifiedDate", "IsKnowHowProtected", "IsConsistent", "IsWriteProtected",
            "IsFailsafe",
        };

        /// <summary>Gli attributi riletti dopo l'export per scoprire modifiche fatte nel frattempo.</summary>
        internal static readonly string[] DateAttributes =
        {
            "CodeModifiedDate", "InterfaceModifiedDate", "ModifiedDate", "CompileDate", "ParameterModified", "StructureModified",
        };

        private readonly Dictionary<string, HashSet<string>> _available = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly string _culture;

        /// <param name="preferredCulture">Lingua dei testi multilingua (es. it-IT); null = la prima non vuota.</param>
        internal AttributeReader(string preferredCulture = null)
        {
            _culture = preferredCulture;
        }

        internal Dictionary<string, string> Read(IEngineeringObject item, string[] wanted)
        {
            return Read(item, wanted, null);
        }

        /// <summary>
        /// Gli attributi disponibili si leggono una volta per chiave: tipo CLR piu'
        /// <paramref name="typeKey"/>. Per l'hardware serve il TypeIdentifier: ogni
        /// modulo e' un DeviceItem, ma un rack e un modulo I/O hanno attributi diversi.
        /// </summary>
        internal Dictionary<string, string> Read(IEngineeringObject item, string[] wanted, string typeKey)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            string key = item.GetType().FullName + "|" + (typeKey ?? "");
            HashSet<string> present;
            if (!_available.TryGetValue(key, out present))
            {
                present = new HashSet<string>(StringComparer.Ordinal);
                foreach (EngineeringAttributeInfo info in item.GetAttributeInfos())
                {
                    present.Add(info.Name);
                }

                _available[key] = present;
            }

            List<string> names = new List<string>();
            foreach (string name in wanted)
            {
                if (present.Contains(name))
                {
                    names.Add(name);
                }
            }

            if (names.Count == 0)
            {
                return result;
            }

            IList<object> values;
            try
            {
                values = item.GetAttributes(names);
            }
            catch (Exception ex)
            {
                // Un attributo dichiarato ma non leggibile su questo oggetto: si va uno a uno.
                Console.Error.WriteLine("GetAttributes in blocco fallita (" + ex.Message + "), lettura singola");
                values = new List<object>();
                foreach (string name in names)
                {
                    try
                    {
                        values.Add(item.GetAttribute(name));
                    }
                    catch (Exception)
                    {
                        values.Add(null);
                    }
                }
            }

            for (int i = 0; i < names.Count && i < values.Count; i++)
            {
                string text = Format(values[i], _culture);
                if (text != null)
                {
                    result[names[i]] = text;
                }
            }

            return result;
        }

        /// <summary>Tutti i nomi di attributo di un oggetto (per --dump-attributes).</summary>
        internal static List<string> Names(IEngineeringObject item)
        {
            List<string> names = new List<string>();
            foreach (EngineeringAttributeInfo info in item.GetAttributeInfos())
            {
                names.Add(info.Name);
            }

            names.Sort(StringComparer.Ordinal);
            return names;
        }

        /// <summary>Testo multilingua nella lingua preferita, altrimenti il primo non vuoto.</summary>
        internal static string Text(MultilingualText text, string culture)
        {
            if (text == null)
            {
                return null;
            }

            string first = null;
            foreach (MultilingualTextItem item in text.Items)
            {
                string value;
                try
                {
                    value = item.Text;
                }
                catch (Exception)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                try
                {
                    if (culture != null && item.Language != null && item.Language.Culture != null &&
                        item.Language.Culture.Name.Equals(culture, StringComparison.OrdinalIgnoreCase))
                    {
                        return value;
                    }
                }
                catch (Exception)
                {
                    // Lingua non leggibile: vale come testo di ripiego.
                }

                first = first ?? value;
            }

            return first;
        }

        internal static string Format(object value, string culture)
        {
            MultilingualText ml = value as MultilingualText;
            return ml != null ? Text(ml, culture) : Format(value);
        }

        /// <summary>
        /// Le date restano con l'indicazione del Kind ("o"): Z = UTC, offset = locale,
        /// niente = non specificato. Serve a misurare cosa restituisce TIA.
        /// </summary>
        internal static string Format(object value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is DateTime)
            {
                return ((DateTime)value).ToString("o", CultureInfo.InvariantCulture);
            }

            if (value is bool)
            {
                return (bool)value ? "true" : "false";
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }
}
