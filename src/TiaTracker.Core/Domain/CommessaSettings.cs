using System.Text.Json;

namespace TiaTracker.Core.Domain;

/// <summary>
/// Impostazioni di una commessa, salvate in JSON (commessa.settings_json):
/// automazioni, calendario della pianificazione, versione di riferimento.
/// Campi nuovi con un default: un JSON vecchio resta leggibile.
/// </summary>
public sealed class CommessaSettings
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Versione di riferimento scelta a mano (null = automatica: In lavoro, poi caricata, poi la piu' recente).</summary>
    public long? ReferenceVersionId { get; set; }

    /// <summary>Dopo un export hardware della versione di riferimento la Lista IP si allinea da sola.</summary>
    public bool AutoIpSync { get; set; } = true;

    /// <summary>Dopo uno snapshot della versione di riferimento i documenti in Elenchi\ si rigenerano.</summary>
    public bool AutoDocuments { get; set; } = true;

    /// <summary>Pianificazione: sabato e domenica contano come giorni lavorativi.</summary>
    public bool WorkOnWeekends { get; set; }

    public static CommessaSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new CommessaSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<CommessaSettings>(json, Options) ?? new CommessaSettings();
        }
        catch (JsonException)
        {
            return new CommessaSettings();
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);
}
