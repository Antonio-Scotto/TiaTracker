namespace TiaTracker.Core.Domain;

public static class SnapshotSources
{
    public const string Attach = "attach";
    public const string File = "file";
    public const string Backup = "backup";
    public const string Rar = "rar";

    /// <summary>Cartella di export XML gia' esistente (senza TIA).</summary>
    public const string Export = "export";
}

public sealed class Snapshot
{
    public long Id { get; set; }
    public long VersionId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string Source { get; set; } = SnapshotSources.Attach;
    public string? SourceDetail { get; set; }
    public string? ProjectPath { get; set; }
    public string? PlcName { get; set; }

    /// <summary>IsModified al momento dell'attach; null per gli snapshot da file.</summary>
    public bool? ProjectModified { get; set; }

    /// <summary>Ultimo salvataggio della versione quando e' stato preso lo snapshot.</summary>
    public DateTime? ProjectSavedUtc { get; set; }

    public string? OnlineState { get; set; }
    public int NormVersion { get; set; }
    public string? WorkerVersion { get; set; }
    public int? TiaMajor { get; set; }
    public int ItemCount { get; set; }
    public int FailedCount { get; set; }
    public bool Partial { get; set; }
    public string? ManifestHash { get; set; }
    public string? TimingsJson { get; set; }
    public string? Note { get; set; }
}

public sealed class SnapshotItem
{
    public long Id { get; set; }
    public long SnapshotId { get; set; }
    public string Family { get; set; } = "block";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string GroupPath { get; set; } = "";
    public int? Number { get; set; }
    public string? Language { get; set; }
    public string? InstanceOf { get; set; }
    public string State { get; set; } = "ok";
    public string? Reason { get; set; }
    public string? AttrsJson { get; set; }
    public string? CodeModifiedAttr { get; set; }
    public string? InterfaceModifiedAttr { get; set; }
    public string? CompiledAttr { get; set; }

    public string? HAll { get; set; }
    public string? HCode { get; set; }
    public string? HIface { get; set; }
    public string? HInit { get; set; }
    public string? HText { get; set; }
    public string? HMeta { get; set; }

    /// <summary>Hash nel CAS dell'XML (senza DocumentInfo) e del sorgente SCL.</summary>
    public string? XmlRef { get; set; }
    public string? SclRef { get; set; }

    /// <summary>JSON con un elemento per rete: indice, titolo, hash codice e testo.</summary>
    public string? UnitsJson { get; set; }
    public bool ChangedDuringExport { get; set; }

    public bool IsComparable => State == "ok" && HAll != null;
    public string Key => Family + "|" + Name.ToUpperInvariant();
}

/// <summary>Una rete (CompileUnit) per il confronto LAD/FBD.</summary>
public sealed record UnitInfo(int Index, string? Title, string HCode, string HText, string Language);

/// <summary>
/// Un export di hardware, rete e tag da TIA (hardware.json con i tag dentro),
/// salvato nel content store: da qui nascono Lista IP, Lista Hardware e Lista IO.
/// </summary>
public sealed class HwSnapshot
{
    public long Id { get; set; }
    public long VersionId { get; set; }

    /// <summary>Lo snapshot dei blocchi fatto nello stesso export, se c'e'.</summary>
    public long? SnapshotId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string Source { get; set; } = SnapshotSources.Attach;
    public string? SourceDetail { get; set; }
    public string? ProjectPath { get; set; }
    public bool? ProjectModified { get; set; }
    public string? WorkerVersion { get; set; }
    public int? TiaMajor { get; set; }
    public int Format { get; set; }
    public string DataHash { get; set; } = "";
    public int DeviceCount { get; set; }
    public int ModuleCount { get; set; }
    public int IpCount { get; set; }
    public int TagCount { get; set; }
    public int IoTagCount { get; set; }
    public bool Partial { get; set; }
    public string? WarningsJson { get; set; }
    public string? TimingsJson { get; set; }
}
