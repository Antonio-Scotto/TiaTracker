using TiaTracker.Contracts;

namespace TiaTracker.Core.Workers;

/// <summary>
/// Il testo per l'utente di un errore del worker, uguale per ogni comando
/// (instances, snapshot, hardware...). Prima l'errore di "instances" usciva
/// grezzo e il worker mancante si presentava come "DLL Openness non trovate".
/// </summary>
public static class WorkerErrorText
{
    /// <param name="exitCode">Codice di uscita (ExitCodes).</param>
    /// <param name="errorCode">Campo code dell'ultima riga "error", se c'e'.</param>
    /// <param name="detail">Testo dell'errore o ultima riga di stderr, mostrato tra parentesi.</param>
    /// <param name="tiaMajor">Versione di TIA del worker usato.</param>
    public static string Explain(int exitCode, string? errorCode, string? detail, int tiaMajor)
    {
        string tia = "TIA V" + tiaMajor;
        return exitCode switch
        {
            ExitCodes.WorkerNotFound =>
                "Il worker di TiaTracker per " + tia + " non e' stato trovato: senza worker gli snapshot da TIA non funzionano. " +
                "Nel pacchetto completo sta in worker\\" + WorkerLocator.FolderName(tiaMajor) + " accanto a TiaTracker.exe. " +
                "Aprire \"Diagnostica\" (barra in alto) per vedere dove e' stato cercato e indicarne la cartella.",
            ExitCodes.Busy =>
                "C'e' gia' un lavoro in corso con TIA (barra in basso): attendere che finisca e riprovare.",
            ExitCodes.OpennessNotFound =>
                "Le DLL Openness di " + tia + " non sono state trovate: serve TIA Portal " + tia.Substring(4) +
                " con l'opzione Openness. Se e' installato in un percorso non standard, indicare la cartella PublicAPI in \"Diagnostica\"." +
                Detail(detail),
            ExitCodes.AccessDenied =>
                "Accesso Openness negato. L'utente Windows deve essere nel gruppo \"Siemens TIA Openness\" " +
                "(dopo averlo aggiunto bisogna disconnettersi e riaccedere) e confermare l'accesso nella finestra di TIA." + Detail(detail),
            ExitCodes.Timeout when errorCode == "access-prompt-timeout" =>
                "TIA Portal ha chiesto l'autorizzazione Openness e nessuno ha risposto entro 120 s. " +
                "Confermare la finestra \"Openness access\" in TIA (meglio \"Si' a tutti\") e riprovare.",
            ExitCodes.Timeout =>
                "Il worker non ha risposto in tempo." + Detail(detail),
            ExitCodes.PlcOnline =>
                "Il PLC e' online in TIA Portal: andare offline e riprovare." + Detail(detail),
            ExitCodes.InstanceNotFound =>
                "TIA Portal o il progetto non sono piu' aperti." + Detail(detail),
            ExitCodes.OpenFailed when MaxPathLength(detail) is int max =>
                "TIA Portal apre solo progetti in percorsi di al massimo " + max + " caratteri e questo era piu' lungo. " +
                "Accorciare il nome della cartella o del progetto, oppure spostare la cartella dati di TiaTracker in un percorso corto." +
                Detail(detail),
            ExitCodes.OpenFailed =>
                "TIA Portal non e' riuscito ad aprire il progetto (gia' aperto altrove, licenza o versione diversa?)." + Detail(detail),
            ExitCodes.TiaCrashed =>
                "TIA Portal non risponde o si e' chiuso durante il lavoro." + Detail(detail),
            ExitCodes.Cancelled => "Annullato.",
            _ => Capitalize(ExitCodes.Describe(exitCode)) + "." + Detail(detail),
        };
    }

    /// <summary>"... is too long. A maximum of 143 characters is allowed" (anche in italiano e tedesco): il limite, o null.</summary>
    private static int? MaxPathLength(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
        {
            return null;
        }

        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(detail,
            @"(?:maximum of|massimo(?: di)?|maximal|max\.?)\s*(\d{2,3})\s*(?:characters|character|caratteri|Zeichen)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    private static string Detail(string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? "" : " (" + detail.Trim().TrimEnd('.') + ")";

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
}
