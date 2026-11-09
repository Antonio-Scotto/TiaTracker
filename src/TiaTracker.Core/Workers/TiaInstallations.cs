using System.Globalization;
using System.Text.RegularExpressions;

namespace TiaTracker.Core.Workers;

/// <summary>Una cartella PublicAPI di TIA con le DLL Openness.</summary>
/// <param name="ApiVersion">Versione dell'API come nella cartella (V15.1, V18, V21).</param>
/// <param name="Folder">Cartella che contiene la DLL (per V19+ la sottocartella net48).</param>
/// <param name="Dll">Siemens.Engineering.Base.dll (V19+) o Siemens.Engineering.dll (fino a V18).</param>
public sealed record OpennessApi(string ApiVersion, double Number, string Folder, string Dll)
{
    /// <summary>Il worker che la usa: v21 per Siemens.Engineering.Base, v18 per la DLL unica.</summary>
    public int WorkerMajor => Dll.Equals(TiaInstallations.BaseDll, StringComparison.OrdinalIgnoreCase) ? 21 : 18;
}

/// <summary>
/// Le DLL Openness installate sul PC, cercate come fa il worker
/// (OpennessResolver): Siemens\Automation\Portal V*\PublicAPI\V*, salvo le
/// cartelle *.AddIn, prima net48\ e poi la cartella stessa.
/// </summary>
public static class TiaInstallations
{
    public const string BaseDll = "Siemens.Engineering.Base.dll";
    public const string SingleDll = "Siemens.Engineering.dll";

    private static readonly Regex VersionSegment = new(@"^V(\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> DefaultRoots()
    {
        List<string> roots = new();
        foreach (Environment.SpecialFolder f in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            string p = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(p) && !roots.Contains(p, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(p);
            }
        }

        return roots;
    }

    public static List<OpennessApi> Find() => Find(DefaultRoots());

    public static List<OpennessApi> Find(IEnumerable<string> programFilesRoots)
    {
        List<OpennessApi> found = new();
        foreach (string root in programFilesRoots)
        {
            string automation = Path.Combine(root, "Siemens", "Automation");
            if (!Directory.Exists(automation))
            {
                continue;
            }

            foreach (string portal in SafeDirs(automation, "Portal V*"))
            {
                string publicApi = Path.Combine(portal, "PublicAPI");
                foreach (string apiDir in SafeDirs(publicApi, "V*"))
                {
                    string segment = Path.GetFileName(apiDir);
                    Match m = VersionSegment.Match(segment);
                    if (!m.Success)
                    {
                        continue;
                    }

                    double number = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    foreach (string folder in new[] { Path.Combine(apiDir, "net48"), apiDir })
                    {
                        string? dll = File.Exists(Path.Combine(folder, BaseDll)) ? BaseDll
                            : File.Exists(Path.Combine(folder, SingleDll)) ? SingleDll
                            : null;
                        if (dll != null)
                        {
                            found.Add(new OpennessApi(segment.ToUpperInvariant(), number, folder, dll));
                            break;
                        }
                    }
                }
            }
        }

        return found.OrderByDescending(a => a.Number).ToList();
    }

    /// <summary>La versione piu' alta utilizzabile dal worker di quel major (21 → Base.dll, 18 → DLL unica).</summary>
    public static OpennessApi? BestFor(IEnumerable<OpennessApi> apis, int workerMajor) =>
        apis.Where(a => a.WorkerMajor == (workerMajor <= 18 ? 18 : 21)).OrderByDescending(a => a.Number).FirstOrDefault();

    private static IEnumerable<string> SafeDirs(string parent, string pattern)
    {
        try
        {
            return Directory.Exists(parent)
                ? Directory.GetDirectories(parent, pattern).Where(d => !d.EndsWith(".AddIn", StringComparison.OrdinalIgnoreCase))
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}
