namespace TiaTracker.Core.Domain;

public sealed class Commessa
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Customer { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedUtc { get; set; }

    /// <summary>Cartella TiaTrackerOut; null = TiaTrackerOut nella prima cartella di scansione.</summary>
    public string? OutputDir { get; set; }

    /// <summary>Cartella di rete dove salvare i backup .rar delle versioni.</summary>
    public string? BackupDir { get; set; }

    /// <summary>Impostazioni della commessa in JSON (CommessaSettings): automazioni, calendario, riferimento.</summary>
    public string? SettingsJson { get; set; }

    public string Display => string.IsNullOrWhiteSpace(Name) || Name == Code ? Code : Code + " " + Name;
}

/// <summary>Una riga del layout di rete della commessa.</summary>
public sealed class IpDevice
{
    public long Id { get; set; }
    public long CommessaId { get; set; }
    public int Sort { get; set; }
    public string? Network { get; set; }
    public string? Ip { get; set; }
    public string? Subnet { get; set; }
    public string? Gateway { get; set; }
    public string? Name { get; set; }
    public string? Kind { get; set; }
    public string? ProfinetName { get; set; }
    public string? Location { get; set; }
    public string? Mac { get; set; }
    public string? Notes { get; set; }
    public DateTime UpdatedUtc { get; set; }

    /// <summary>"manuale" o "tia" (riga letta dal progetto TIA: i campi di rete li aggiorna TIA).</summary>
    public string Source { get; set; } = IpSources.Manual;

    /// <summary>Chiave della riga in TIA (dispositivo/interfaccia#nodo) per riconoscerla agli export successivi.</summary>
    public string? TiaKey { get; set; }
    public long? TiaVersionId { get; set; }
    public DateTime? TiaSeenUtc { get; set; }

    /// <summary>Era in TIA e all'ultimo export non c'e' piu': la riga resta, segnata.</summary>
    public bool TiaMissing { get; set; }

    public bool IsFromTia => Source == IpSources.Tia;
}

public static class IpSources
{
    public const string Manual = "manuale";
    public const string Tia = "tia";

    /// <summary>Trovata in rete da PRONETA (file CSV o XML).</summary>
    public const string Proneta = "proneta";

    public static string Normalize(string? source) => source is Tia or Proneta ? source : Manual;
}

/// <summary>
/// Cartella da scansionare per trovare le versioni di una commessa. Il
/// percorso e' l'unico dato legato al PC: "Riaggancia cartella" lo cambia e
/// tutti i percorsi delle versioni, relativi a questa radice, tornano validi.
/// </summary>
public sealed class ScanRoot
{
    public long Id { get; set; }
    public long CommessaId { get; set; }
    public string Path { get; set; } = "";
    public string? NameRegex { get; set; }
    public string? HistoryGlobs { get; set; }
    public DateTime? LastScanUtc { get; set; }
}

public sealed class ProjectVersion
{
    public long Id { get; set; }
    public long CommessaId { get; set; }
    public long? ScanRootId { get; set; }
    public string Label { get; set; } = "";
    public string SortKey { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public int? TiaMajor { get; set; }
    public string? TiaVersionText { get; set; }

    public string? FolderRel { get; set; }
    public string? ProjectFileRel { get; set; }
    public string? RarRel { get; set; }
    public string? BackupRel { get; set; }
    public bool HasFolder { get; set; }
    public bool HasRar { get; set; }
    public bool HasBackup { get; set; }
    public int BackupCount { get; set; }
    public DateTime? LastBackupUtc { get; set; }
    public DateTime? LastSavedUtc { get; set; }

    /// <summary>Stato scelto dall'utente (None = nessuno: se superata si mostra Vecchia).</summary>
    public VersionState State { get; set; }
    public DateTime? StateUtc { get; set; }

    /// <summary>Nota dello stato, es. "FAT" o "SAT 12/10" per In collaudo.</summary>
    public string? StateNote { get; set; }

    // Flag storici, riallineati dal codice a stato e carichi (version_load): nessun file
    // dice quale versione e' sul PLC. IsLoaded = ha almeno un carico corrente.
    public bool IsLoaded { get; set; }
    public DateTime? LoadedUtc { get; set; }
    public string? LoadedPlc { get; set; }
    public string? LoadedNote { get; set; }
    public bool IsActive { get; set; }
    public bool IsArchived { get; set; }
    public string? Note { get; set; }

    public DateTime FirstSeenUtc { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public bool Missing { get; set; }
}

/// <summary>Stato di lavoro di una versione, scelto dall'utente (menu tasto destro).</summary>
public enum VersionState
{
    None,
    InLavoro,
    DaCaricare,
    InCollaudo,
    Caricata,
    Vecchia,
    Archiviata,
    Scartata,
}

public static class VersionStates
{
    /// <summary>Nell'ordine del menu.</summary>
    public static readonly IReadOnlyList<VersionState> All = new[]
    {
        VersionState.InLavoro, VersionState.DaCaricare, VersionState.InCollaudo, VersionState.Caricata,
        VersionState.Vecchia, VersionState.Archiviata, VersionState.Scartata,
    };

    public static string? ToDb(VersionState s) => s switch
    {
        VersionState.InLavoro => "in_lavoro",
        VersionState.DaCaricare => "da_caricare",
        VersionState.InCollaudo => "in_collaudo",
        VersionState.Caricata => "caricata",
        VersionState.Vecchia => "vecchia",
        VersionState.Archiviata => "archiviata",
        VersionState.Scartata => "scartata",
        _ => null,
    };

    /// <summary>Valori sconosciuti (DB scritto da una versione piu' nuova) diventano None.</summary>
    public static VersionState FromDb(string? s) => s switch
    {
        "in_lavoro" => VersionState.InLavoro,
        "da_caricare" => VersionState.DaCaricare,
        "in_collaudo" => VersionState.InCollaudo,
        "caricata" => VersionState.Caricata,
        "vecchia" => VersionState.Vecchia,
        "archiviata" => VersionState.Archiviata,
        "scartata" => VersionState.Scartata,
        _ => VersionState.None,
    };

    public static string Italian(VersionState s) => s switch
    {
        VersionState.InLavoro => "In lavoro",
        VersionState.DaCaricare => "Da caricare",
        VersionState.InCollaudo => "In collaudo",
        VersionState.Caricata => "Caricata",
        VersionState.Vecchia => "Vecchia",
        VersionState.Archiviata => "Archiviata",
        VersionState.Scartata => "Scartata",
        _ => "",
    };

    public static string Description(VersionState s) => s switch
    {
        VersionState.InLavoro => "Versione su cui si sta lavorando",
        VersionState.DaCaricare => "Pronta, in attesa di essere caricata sul PLC",
        VersionState.InCollaudo => "In prova in officina o in campo (FAT/SAT)",
        VersionState.Caricata => "Caricata sul PLC: la precedente sullo stesso PLC diventa Vecchia",
        VersionState.Vecchia => "Superata da una versione piu' recente",
        VersionState.Archiviata => "Solo archivio: nascosta se si tolgono le vecchie",
        VersionState.Scartata => "Abbandonata: resta nella storia ma fuori da confronti e documenti automatici",
        _ => "Nessuno stato: diventa Vecchia da sola quando esce una versione piu' recente",
    };

    public static Tone ToneOf(VersionState s) => s switch
    {
        VersionState.InLavoro => Tone.Accent,
        VersionState.DaCaricare => Tone.Warning,
        VersionState.InCollaudo => Tone.Purple,
        VersionState.Caricata => Tone.Success,
        VersionState.Vecchia => Tone.Neutral,
        VersionState.Archiviata => Tone.Muted,
        VersionState.Scartata => Tone.Danger,
        _ => Tone.Muted,
    };

    /// <summary>Fuori dal lavoro corrente: nascoste dal filtro "mostra vecchie" spento.</summary>
    public static bool IsRetired(VersionState s) => s is VersionState.Vecchia or VersionState.Archiviata or VersionState.Scartata;
}

/// <summary>
/// Un carico di una versione su un PLC: corrente finche' EndedUtc e' null. Una
/// versione puo' essere su piu' PLC; su un PLC c'e' al massimo un carico corrente.
/// </summary>
public sealed class VersionLoad
{
    public long Id { get; set; }
    public long CommessaId { get; set; }
    public long VersionId { get; set; }

    /// <summary>"" = PLC senza nome.</summary>
    public string Plc { get; set; } = "";
    public DateTime LoadedUtc { get; set; }
    public string? Note { get; set; }
    public long? ProofSnapshotId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public long? EndedByVersionId { get; set; }
    public string? EndedReason { get; set; }

    public bool IsCurrent => EndedUtc == null;

    public string PlcDisplay => Plc.Length == 0 ? "PLC" : Plc;
}

public sealed class VersionEvent
{
    public long Id { get; set; }
    public long VersionId { get; set; }
    public DateTime Utc { get; set; }
    public string Kind { get; set; } = "";
    public string? Detail { get; set; }
}

public enum ChangeScope
{
    Plc,
    Ignition,
    Altro,
}

/// <summary>
/// Ciclo di vita di una modifica su una versione. Compiled senza Saved e'
/// il caso critico "importato e compilato, progetto NON salvato".
/// </summary>
public enum ChangeState
{
    Planned,
    Imported,
    Compiled,
    Saved,
    Dropped,
}

public sealed class Change
{
    public long Id { get; set; }
    public long CommessaId { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? Motivation { get; set; }
    public DateOnly? Date { get; set; }
    public ChangeScope Scope { get; set; } = ChangeScope.Plc;
    public bool Draft { get; set; }
    public string? Confidence { get; set; }

    /// <summary>Estratto originale del documento da cui la bozza e' stata importata.</summary>
    public string? Excerpt { get; set; }

    public long? ImportSourceId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public List<ChangeBlock> Blocks { get; set; } = new();
    public List<ChangeVersion> Versions { get; set; } = new();
}

public sealed class ChangeBlock
{
    public string BlockName { get; set; } = "";
    public string? Family { get; set; }

    /// <summary>nuovo / sovrascrive / modifica / elimina, libero.</summary>
    public string? Action { get; set; }

    public string? Note { get; set; }
}

public sealed class ChangeVersion
{
    public long ChangeId { get; set; }
    public long VersionId { get; set; }
    public ChangeState State { get; set; }
    public DateTime? StateUtc { get; set; }
    public int? Errors { get; set; }
    public int? Warnings { get; set; }
    public string? Note { get; set; }
    public long? EvidenceRunId { get; set; }
}

public sealed class ChangeEvent
{
    public long Id { get; set; }
    public long ChangeId { get; set; }
    public long? VersionId { get; set; }
    public DateTime Utc { get; set; }
    public string Kind { get; set; } = "";
    public string? OldState { get; set; }
    public string? NewState { get; set; }
    public string? Detail { get; set; }
}

public static class ChangeStates
{
    public static string ToDb(ChangeState s) => s switch
    {
        ChangeState.Planned => "planned",
        ChangeState.Imported => "imported",
        ChangeState.Compiled => "compiled",
        ChangeState.Saved => "saved",
        ChangeState.Dropped => "dropped",
        _ => "planned",
    };

    public static ChangeState FromDb(string? s) => s switch
    {
        "imported" => ChangeState.Imported,
        "compiled" => ChangeState.Compiled,
        "saved" => ChangeState.Saved,
        "dropped" => ChangeState.Dropped,
        _ => ChangeState.Planned,
    };

    public static string Italian(ChangeState s) => s switch
    {
        ChangeState.Planned => "Pianificata",
        ChangeState.Imported => "Importata",
        ChangeState.Compiled => "Compilata",
        ChangeState.Saved => "Salvata",
        ChangeState.Dropped => "Scartata",
        _ => s.ToString(),
    };

    public static string ScopeToDb(ChangeScope s) => s switch
    {
        ChangeScope.Ignition => "ignition",
        ChangeScope.Altro => "altro",
        _ => "plc",
    };

    public static ChangeScope ScopeFromDb(string? s) => s switch
    {
        "ignition" => ChangeScope.Ignition,
        "altro" => ChangeScope.Altro,
        _ => ChangeScope.Plc,
    };

    public static string ScopeItalian(ChangeScope s) => s switch
    {
        ChangeScope.Ignition => "Ignition",
        ChangeScope.Altro => "Altro",
        _ => "PLC",
    };
}
