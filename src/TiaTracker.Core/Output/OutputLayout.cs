using System.Globalization;
using System.Text;
using TiaTracker.Core.Domain;

namespace TiaTracker.Core.Output;

/// <summary>
/// La cartella TiaTrackerOut di una commessa: tutto cio' che TiaTracker
/// produce, in ordine, accanto ai progetti.
/// <code>
/// TiaTrackerOut\
///   LEGGIMI.txt
///   Snapshot\&lt;versione&gt;\&lt;aaaammgg_hhmm&gt;_&lt;sorgente&gt;_&lt;plc&gt;\   XML, SCL e manifest dell'export
///   Confronti\&lt;base&gt;_&lt;target&gt;_&lt;aaaammgg_hhmm&gt;.md               report di confronto e riconciliazione
///   Modifiche\registro_modifiche.md / .csv                        registro delle modifiche per versione
///   Rete\layout_ip.md / .csv                                      tabella IP
///   Elenchi\&lt;codice&gt;_Lista_IP|Hardware|IO.docx/.pdf/.csv          liste generate (altre versioni in Elenchi\&lt;versione&gt;\)
///   Documenti\                                                    documenti della commessa (liberi)
///   TiaTracker\commessa.json                                      dati della commessa in TiaTracker
/// </code>
/// </summary>
public sealed class OutputLayout
{
    public const string DefaultFolderName = "TiaTrackerOut";

    public OutputLayout(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public string Snapshots => Path.Combine(Root, "Snapshot");

    public string Compares => Path.Combine(Root, "Confronti");

    public string Changes => Path.Combine(Root, "Modifiche");

    public string Network => Path.Combine(Root, "Rete");

    public string Documents => Path.Combine(Root, "Documenti");

    /// <summary>Liste generate (IP, hardware, IO, registro): TiaTracker le riscrive a ogni generazione.</summary>
    public string Lists => Path.Combine(Root, "Elenchi");

    public string Data => Path.Combine(Root, "TiaTracker");

    /// <summary>La cartella scelta per la commessa o TiaTrackerOut nella prima cartella di scansione.</summary>
    public static OutputLayout? For(Commessa c, IEnumerable<ScanRoot> roots)
    {
        if (!string.IsNullOrWhiteSpace(c.OutputDir))
        {
            return new OutputLayout(c.OutputDir!);
        }

        ScanRoot? first = roots.FirstOrDefault();
        return first == null ? null : new OutputLayout(Path.Combine(first.Path, DefaultFolderName));
    }

    public void EnsureCreated()
    {
        foreach (string d in new[] { Root, Snapshots, Compares, Changes, Network, Lists, Documents, Data })
        {
            Directory.CreateDirectory(d);
        }

        // Riscritto se manca o se e' quello di una versione precedente (senza Elenchi).
        string readme = Path.Combine(Root, "LEGGIMI.txt");
        if (!File.Exists(readme) || !ReadmeIsCurrent(readme))
        {
            File.WriteAllText(readme, Readme, new UTF8Encoding(true));
        }
    }

    /// <summary>Cartella delle liste: Elenchi\ per la versione di riferimento, Elenchi\&lt;versione&gt;\ per le altre.</summary>
    public string ListsDir(string? versionLabel) =>
        string.IsNullOrWhiteSpace(versionLabel) ? Lists : Path.Combine(Lists, Safe(versionLabel));

    /// <summary>Un LEGGIMI scritto a mano (non comincia con la nostra intestazione) non si tocca.</summary>
    private static bool ReadmeIsCurrent(string path)
    {
        try
        {
            string text = File.ReadAllText(path);
            return !text.StartsWith("TiaTrackerOut - cartella generata da TiaTracker", StringComparison.Ordinal) ||
                   text.Contains("Elenchi\\", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return true;
        }
    }

    public string SnapshotDir(string versionLabel, DateTime createdUtc, string source, string? plc) =>
        Path.Combine(Snapshots, Safe(versionLabel),
            createdUtc.ToLocalTime().ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture) + "_" + Safe(source) + (plc != null ? "_" + Safe(plc) : ""));

    public string CompareFile(string baseLabel, string targetLabel, string? plc, DateTime utc) =>
        Path.Combine(Compares, Safe(baseLabel) + "_" + Safe(targetLabel) + (plc != null ? "_" + Safe(plc) : "") + "_" +
                               utc.ToLocalTime().ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture) + ".md");

    public static string Safe(string s)
    {
        StringBuilder sb = new(s.Length);
        foreach (char ch in s)
        {
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 || ch == ' ' ? '_' : ch);
        }

        return sb.ToString();
    }

    private const string Readme =
        "TiaTrackerOut - cartella generata da TiaTracker\r\n" +
        "================================================\r\n\r\n" +
        "Snapshot\\    export dei progetti TIA (XML, SCL, manifest.json), per versione e data\r\n" +
        "Confronti\\   report dei confronti fra versioni con la riconciliazione delle modifiche\r\n" +
        "Modifiche\\   registro delle modifiche (stato per versione), rigenerato da TiaTracker\r\n" +
        "Rete\\        layout IP della commessa (anche in CSV per Excel/Google Fogli)\r\n" +
        "Elenchi\\     Lista IP, Lista hardware, Lista IO e registro in DOCX, PDF e CSV, generati dai dati\r\n" +
        "             letti da TIA (versione di riferimento; le altre versioni in Elenchi\\<versione>\\)\r\n" +
        "Documenti\\   documenti della commessa: cartella libera, TiaTracker non la tocca\r\n" +
        "TiaTracker\\  commessa.json: i dati della commessa in TiaTracker (versioni, modifiche, IP)\r\n\r\n" +
        "I file in Modifiche, Rete, Elenchi e TiaTracker vengono riscritti da TiaTracker: non modificarli a mano\r\n" +
        "(note e righe aggiunte si scrivono in TiaTracker, scheda Documenti, e restano a ogni rigenerazione).\r\n";
}
