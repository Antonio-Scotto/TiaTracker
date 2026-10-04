using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using TiaTracker.Contracts;

namespace TiaTracker.Core.Snapshots;

/// <summary>
/// Manifest costruito da una cartella di export XML gia' esistente (toolkit
/// ClaudeForTia: una sottocartella per gruppo di primo livello, _Types per gli
/// UDT). Permette di registrare a posteriori gli export fatti senza TiaTracker.
/// Gli attributi (date di compilazione) non ci sono: "solo ricompilato" non si distingue.
/// </summary>
public static class ExportFolderManifest
{
    public const string Source = "export";

    public static SnapshotManifest Build(string folder, string? plcName)
    {
        SnapshotManifest m = new()
        {
            Source = Source,
            ProjectPath = folder,
            ProjectName = Path.GetFileName(folder.TrimEnd('\\')),
            PlcName = plcName,
            StartedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            FinishedUtc = Directory.GetLastWriteTimeUtc(folder).ToString("o", CultureInfo.InvariantCulture),
            WorkerVersion = "cartella",
        };

        foreach (string file in Directory.EnumerateFiles(folder, "*.xml", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            ManifestItem? item = Describe(file);
            if (item == null)
            {
                m.Warnings.Add("Non e' un export di blocco: " + Path.GetFileName(file));
                continue;
            }

            string rel = Path.GetRelativePath(folder, file);
            string group = Path.GetDirectoryName(rel) ?? "";
            item.GroupPath = group.Equals("_Types", StringComparison.OrdinalIgnoreCase) || group.Equals("_Root", StringComparison.OrdinalIgnoreCase)
                ? ""
                : group.Replace('\\', '/');
            item.XmlFile = file;
            m.Items.Add(item);
        }

        // Stesso nome in due cartelle (export ripetuti): vale il primo.
        m.Items = m.Items.GroupBy(i => i.Family + "|" + i.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        return m;
    }

    private static ManifestItem? Describe(string file)
    {
        XElement? block;
        try
        {
            using XmlReader reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            XDocument doc = XDocument.Load(reader);
            block = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName.StartsWith("SW.", StringComparison.Ordinal));
        }
        catch (XmlException)
        {
            return null;
        }

        if (block == null)
        {
            return null;
        }

        XElement? attrs = block.Element("AttributeList");
        string element = block.Name.LocalName;
        string kind = element switch
        {
            "SW.Blocks.OB" => "OB",
            "SW.Blocks.FB" => "FB",
            "SW.Blocks.FC" => "FC",
            "SW.Blocks.GlobalDB" => "GlobalDB",
            "SW.Blocks.InstanceDB" => "InstanceDB",
            "SW.Blocks.ArrayDB" => "ArrayDB",
            "SW.Types.PlcStruct" => "UDT",
            _ => element.Substring(element.LastIndexOf('.') + 1),
        };

        ManifestItem item = new()
        {
            Family = element.StartsWith("SW.Types", StringComparison.Ordinal) ? Families.Type : Families.Block,
            Kind = kind,
            Name = attrs?.Element("Name")?.Value ?? Path.GetFileNameWithoutExtension(file),
            Language = attrs?.Element("ProgrammingLanguage")?.Value,
            InstanceOf = attrs?.Element("InstanceOfName")?.Value,
            State = ItemStates.Ok,
        };

        if (int.TryParse(attrs?.Element("Number")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
        {
            item.Number = n;
        }

        return item;
    }

    /// <summary>Scrive il manifest nella cartella di lavoro e ne restituisce il percorso.</summary>
    public static string Write(SnapshotManifest m, string workDir)
    {
        Directory.CreateDirectory(workDir);
        string path = Path.Combine(workDir, "manifest.json");
        File.WriteAllText(path, MiniJson.Serialize(m, indented: true));
        return path;
    }
}
