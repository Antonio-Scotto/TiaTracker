using System.Text.Json;
using TiaTracker.Contracts;

namespace TiaTracker.Core.Hardware;

/// <summary>
/// Legge hardware.json del worker e ci mette dentro i tag delle tabelle
/// esportate in XML, cosi' il risultato e' autosufficiente: si salva nel
/// content store e non servono piu' i file dell'export.
/// </summary>
public static class HardwareReader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>hardware.json piu' i tag degli XML (percorsi relativi alla sua cartella).</summary>
    public static HardwareExport Load(string hardwareJsonPath)
    {
        HardwareExport hw = Deserialize(File.ReadAllText(hardwareJsonPath));
        string dir = Path.GetDirectoryName(Path.GetFullPath(hardwareJsonPath))!;
        foreach (TagTableFile table in hw.TagTables)
        {
            if (string.IsNullOrEmpty(table.File))
            {
                continue;
            }

            string file = Path.Combine(dir, table.File.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                (_, List<HwTag> tags) = TagTableXmlParser.Parse(File.ReadAllText(file), hw.Culture);
                table.Tags = tags;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                table.State = ItemStates.Failed;
                table.Reason = "XML illeggibile: " + ex.Message;
                hw.Warnings.Add("Tabella " + table.Plc + "/" + table.Name + ": " + ex.Message);
            }
        }

        return hw;
    }

    public static HardwareExport Deserialize(string json) =>
        JsonSerializer.Deserialize<HardwareExport>(json, Options) ?? new HardwareExport();

    /// <summary>JSON compatto per il content store (i file XML non servono piu').</summary>
    public static string Serialize(HardwareExport hw) => JsonSerializer.Serialize(hw, Options);
}
