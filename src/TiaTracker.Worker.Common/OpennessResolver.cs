using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace TiaTracker.Worker
{
    /// <summary>
    /// Le DLL Siemens non si ridistribuiscono e non stanno accanto all'exe
    /// (Private=False): si risolvono a runtime dall'installazione TIA del PC.
    /// Register() va chiamato PRIMA di toccare qualunque tipo Siemens.
    ///
    /// V19+ (TIA_V21): Siemens.Engineering.Base.dll + Step7.dll in PublicAPI\Vnn\net48.
    /// V15.1..V18 (TIA_V18): un solo Siemens.Engineering.dll in PublicAPI\Vnn.
    /// Override: variabile TIA_PUBLIC_API (o TIA_PUBLIC_API_V18 per il worker V18).
    /// </summary>
    internal static class OpennessResolver
    {
#if TIA_V18
        internal const string ProbeAssembly = "Siemens.Engineering.dll";
        private static readonly string[] EnvVars = { "TIA_PUBLIC_API_V18", "TIA_PUBLIC_API" };
#else
        internal const string ProbeAssembly = "Siemens.Engineering.Base.dll";
        private static readonly string[] EnvVars = { "TIA_PUBLIC_API" };
#endif

        internal static string ApiPath { get; private set; }

        /// <summary>Restituisce un messaggio d'errore se le DLL non si trovano, altrimenti null.</summary>
        internal static string Register()
        {
            try
            {
                ApiPath = ResolveApiPath();
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message;
            }

            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            return null;
        }

        private static string ResolveApiPath()
        {
            foreach (string name in EnvVars)
            {
                string fromEnv = Environment.GetEnvironmentVariable(name);
                if (string.IsNullOrWhiteSpace(fromEnv))
                {
                    continue;
                }

                if (File.Exists(Path.Combine(fromEnv, ProbeAssembly)))
                {
                    return fromEnv;
                }

                // Un override esplicito sbagliato e' un errore, non si ripiega sulla ricerca.
                throw new InvalidOperationException(
                    name + " = '" + fromEnv + "' non contiene " + ProbeAssembly + ".");
            }

            List<string> candidates = new List<string>();
            string[] roots =
            {
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("ProgramFiles(x86)")
            };

            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root))
                {
                    continue;
                }

                string automation = Path.Combine(root, @"Siemens\Automation");
                if (!Directory.Exists(automation))
                {
                    continue;
                }

                foreach (string portal in Directory.GetDirectories(automation, "Portal V*"))
                {
                    string publicApi = Path.Combine(portal, "PublicAPI");
                    if (!Directory.Exists(publicApi))
                    {
                        continue;
                    }

                    foreach (string versionDir in Directory.GetDirectories(publicApi, "V*"))
                    {
                        // Le cartelle *.AddIn contengono solo l'API per gli Add-In.
                        if (versionDir.EndsWith(".AddIn", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        foreach (string dir in new[] { Path.Combine(versionDir, "net48"), versionDir })
                        {
                            if (File.Exists(Path.Combine(dir, ProbeAssembly)))
                            {
                                candidates.Add(dir);
                            }
                        }
                    }
                }
            }

            if (candidates.Count == 0)
            {
                throw new InvalidOperationException(
                    "DLL Openness (" + ProbeAssembly + ") non trovate. Installare TIA Portal con Openness, " +
                    "oppure impostare " + EnvVars[0] + " con la cartella che le contiene.");
            }

            candidates.Sort((a, b) => ExtractVersion(b).CompareTo(ExtractVersion(a)));
            return candidates[0];
        }

        /// <summary>
        /// Versione dell'API dall'ultima parte "Vnn" del percorso: Portal V18
        /// contiene anche V15.1, V16 e V17.
        /// </summary>
        internal static double ExtractVersion(string path)
        {
            string[] parts = path.Split(Path.DirectorySeparatorChar);
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                string part = parts[i];
                if (part.Length > 1 && (part[0] == 'V' || part[0] == 'v'))
                {
                    double value;
                    if (double.TryParse(part.Substring(1), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out value))
                    {
                        return value;
                    }
                }
            }

            return 0;
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            if (name.IndexOf("Siemens.Engineering", StringComparison.OrdinalIgnoreCase) != 0)
            {
                return null;
            }

            string path = Path.Combine(ApiPath, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }
    }
}
