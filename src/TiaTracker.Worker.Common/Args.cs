using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using TiaTracker.Contracts;

namespace TiaTracker.Worker
{
    internal sealed class ArgumentException2 : Exception
    {
        internal ArgumentException2(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// Argomenti da riga di comando, oppure --request file.json (oggetto piatto
    /// con gli stessi nomi senza trattini) per i parametri con spazi e accenti.
    /// </summary>
    internal sealed class Args
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal string Command { get; private set; }

        internal static Args Parse(string[] argv)
        {
            Args a = new Args();
            if (argv.Length == 0)
            {
                throw new ArgumentException2("Comando mancante: hello | instances | probe | snapshot | hardware");
            }

            a.Command = argv[0].ToLowerInvariant();
            for (int i = 1; i < argv.Length; i++)
            {
                string arg = argv[i];
                if (!arg.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException2("Argomento inatteso: " + arg);
                }

                string name = arg.Substring(2);
                bool hasValue = i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal);
                if (hasValue)
                {
                    a._values[name] = argv[++i];
                }
                else
                {
                    a._flags.Add(name);
                }
            }

            string request;
            if (a._values.TryGetValue("request", out request))
            {
                Dictionary<string, string> fromFile;
                try
                {
                    fromFile = MiniJson.ParseFlatObject(File.ReadAllText(request));
                }
                catch (Exception ex) when (ex is IOException || ex is FormatException || ex is IndexOutOfRangeException)
                {
                    throw new ArgumentException2("--request illeggibile: " + ex.Message);
                }

                foreach (KeyValuePair<string, string> kv in fromFile)
                {
                    if (kv.Value == "true")
                    {
                        a._flags.Add(kv.Key);
                    }
                    else if (kv.Value != "false")
                    {
                        a._values[kv.Key] = kv.Value;
                    }
                }
            }

            return a;
        }

        internal string Get(string name)
        {
            string v;
            return _values.TryGetValue(name, out v) ? v : null;
        }

        internal bool Flag(string name)
        {
            return _flags.Contains(name);
        }

        internal int? Int(string name)
        {
            string v = Get(name);
            if (v == null)
            {
                return null;
            }

            int n;
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
            {
                throw new ArgumentException2("--" + name + " deve essere un numero: " + v);
            }

            return n;
        }
    }
}
