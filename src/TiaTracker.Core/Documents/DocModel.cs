namespace TiaTracker.Core.Documents;

/// <summary>I documenti di base della commessa.</summary>
public static class DocKinds
{
    public const string Ip = "ip";
    public const string Hardware = "hardware";
    public const string Io = "io";
    public const string Register = "registro";
    public const string Plan = "piano";

    public static string Title(string kind) => kind switch
    {
        Ip => "Lista IP",
        Hardware => "Lista hardware",
        Io => "Lista IO",
        Register => "Registro modifiche",
        Plan => "Piano attivita'",
        _ => kind,
    };

    /// <summary>Parte del nome del file: AUT123456_Lista_IP.docx.</summary>
    public static string FilePart(string kind) => kind switch
    {
        Ip => "Lista_IP",
        Hardware => "Lista_Hardware",
        Io => "Lista_IO",
        Register => "Registro_modifiche",
        Plan => "Piano_attivita",
        _ => kind,
    };
}

public enum DocAlign
{
    Left,
    Center,
    Right,
}

/// <summary>Una colonna: chiave stabile (righe manuali), intestazione, peso nella larghezza, testo monospazio.</summary>
public sealed record DocColumn(string Key, string Header, double Weight = 1, DocAlign Align = DocAlign.Left, bool Mono = false);

public enum DocRowStyle
{
    Normal,

    /// <summary>Riga di gruppo (stazione, rete): fascia grigia, grassetto, una sola cella.</summary>
    Group,

    /// <summary>Riga attenuata (non piu' in TIA, scorta).</summary>
    Muted,
}

public sealed class DocRow
{
    /// <summary>Chiave stabile della riga per le note; per le righe di gruppo il nome breve del gruppo (colonna Gruppo del CSV).</summary>
    public string Key { get; init; } = "";
    public List<string?> Cells { get; init; } = new();
    public DocRowStyle Style { get; init; }

    /// <summary>Riga aggiunta a mano dall'utente (non viene da TIA).</summary>
    public bool IsManual { get; init; }

    /// <summary>Id della riga manuale nel DB.</summary>
    public long? ManualId { get; init; }

    public static DocRow Group(string text, string? shortName = null) =>
        new() { Style = DocRowStyle.Group, Key = shortName ?? text, Cells = { text } };
}

public sealed class DocTable
{
    public List<DocColumn> Columns { get; init; } = new();
    public List<DocRow> Rows { get; init; } = new();

    /// <summary>Testo da mostrare se non ci sono righe.</summary>
    public string? EmptyText { get; init; }

    /// <summary>Indice della colonna Note (modificabile nell'anteprima), -1 se non c'e'.</summary>
    public int NoteColumn => Columns.FindIndex(c => c.Key == "note");
}

public sealed class DocSection
{
    public string Title { get; init; } = "";
    public List<string> Paragraphs { get; init; } = new();
    public DocTable? Table { get; init; }

    /// <summary>La tabella principale: quella che finisce nel CSV e nell'anteprima.</summary>
    public bool IsPrimary { get; init; }
}

public sealed record DocMeta(string Label, string Value);

/// <summary>
/// Un documento neutro (niente logo, niente carta intestata), uguale per DOCX,
/// PDF e CSV: titolo, dati identificativi, sezioni con testo e tabelle.
/// </summary>
public sealed class DocDocument
{
    public string Kind { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Subtitle { get; init; }
    public List<DocMeta> Meta { get; init; } = new();
    public List<DocSection> Sections { get; init; } = new();
    public bool Landscape { get; init; } = true;

    /// <summary>Nome dei file senza estensione (es. AUT123456_Lista_IP).</summary>
    public string FileStem { get; init; } = "";

    /// <summary>Testo del piede di pagina a sinistra (es. "Lista IP · AUT123456 DEMO").</summary>
    public string Footer { get; init; } = "";

    public DocSection? Primary => Sections.FirstOrDefault(s => s.IsPrimary && s.Table != null) ?? Sections.FirstOrDefault(s => s.Table != null);
}
