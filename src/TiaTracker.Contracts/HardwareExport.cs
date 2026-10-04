using System.Collections.Generic;

namespace TiaTracker.Contracts
{
    /// <summary>
    /// hardware.json scritto dal worker: dispositivi e moduli (con codici
    /// d'ordine, firmware, indirizzi I/O), interfacce di rete (IP, subnet, nome
    /// PROFINET), sistemi IO e tabelle dei tag. Solo tipi che MiniJson
    /// serializza (string, int, long, bool, liste, dizionari di stringhe).
    /// </summary>
    public sealed class HardwareExport
    {
        public const int CurrentFormat = 1;

        public int Format { get; set; } = CurrentFormat;
        public string WorkerVersion { get; set; }
        public int TiaMajor { get; set; }
        public string Source { get; set; }
        public int? Pid { get; set; }
        public string ProjectPath { get; set; }
        public string ProjectName { get; set; }
        public bool? IsModified { get; set; }

        /// <summary>Lingua di riferimento del progetto (es. it-IT), usata per i commenti.</summary>
        public string Culture { get; set; }
        public string StartedUtc { get; set; }
        public string FinishedUtc { get; set; }
        public bool Partial { get; set; }
        public List<HwDevice> Devices { get; set; } = new List<HwDevice>();
        public List<HwSubnet> Subnets { get; set; } = new List<HwSubnet>();
        public List<TagTableFile> TagTables { get; set; } = new List<TagTableFile>();
        public List<string> Warnings { get; set; } = new List<string>();
        public Dictionary<string, long> TimingsMs { get; set; } = new Dictionary<string, long>();
    }

    /// <summary>Un dispositivo (stazione) del progetto.</summary>
    public sealed class HwDevice
    {
        public string Name { get; set; }

        /// <summary>Cartella nella vista dispositivi ("" = radice).</summary>
        public string GroupPath { get; set; }
        public string TypeIdentifier { get; set; }
        public Dictionary<string, string> Attributes { get; set; } = new Dictionary<string, string>();

        /// <summary>I PLC (software) che stanno in questo dispositivo.</summary>
        public List<string> PlcNames { get; set; } = new List<string>();
        public List<HwItem> Items { get; set; } = new List<HwItem>();
    }

    /// <summary>Un elemento hardware: rack, modulo, sottomodulo, interfaccia, porta.</summary>
    public sealed class HwItem
    {
        public string Name { get; set; }

        /// <summary>Nomi dal dispositivo a qui, separati da "/".</summary>
        public string Path { get; set; }
        public int? Position { get; set; }
        public string TypeIdentifier { get; set; }
        public string Classification { get; set; }
        public bool IsBuiltIn { get; set; }

        /// <summary>Nome del PLC se l'elemento e' una CPU.</summary>
        public string PlcName { get; set; }

        /// <summary>OrderNumber, FirmwareVersion, TypeName, Comment, Author (quelli disponibili).</summary>
        public Dictionary<string, string> Attributes { get; set; } = new Dictionary<string, string>();
        public List<HwAddress> Addresses { get; set; } = new List<HwAddress>();
        public List<HwChannelGroup> Channels { get; set; } = new List<HwChannelGroup>();
        public HwInterface Interface { get; set; }
        public List<HwItem> Items { get; set; } = new List<HwItem>();
    }

    /// <summary>Un'area di indirizzi di un modulo (Input, Output, Diagnosis...).</summary>
    public sealed class HwAddress
    {
        public string IoType { get; set; }
        public int StartAddress { get; set; }

        /// <summary>Lunghezza come la restituisce Openness (unita' verificata dall'app con i canali).</summary>
        public int Length { get; set; }
    }

    /// <summary>Canali di un modulo raggruppati per tipo (es. Digital Input x16).</summary>
    public sealed class HwChannelGroup
    {
        public string Type { get; set; }
        public string IoType { get; set; }
        public int Count { get; set; }
    }

    /// <summary>Interfaccia di rete (PROFINET/Ethernet, PROFIBUS...).</summary>
    public sealed class HwInterface
    {
        public string InterfaceType { get; set; }
        public string OperatingMode { get; set; }
        public List<HwNode> Nodes { get; set; } = new List<HwNode>();
        public List<HwIoConnector> Connectors { get; set; } = new List<HwIoConnector>();

        /// <summary>Sistemi IO di cui l'interfaccia e' controller.</summary>
        public List<string> ControlledIoSystems { get; set; } = new List<string>();
    }

    /// <summary>Un nodo di rete: Address (IP), SubnetMask, RouterAddress, PnDeviceName...</summary>
    public sealed class HwNode
    {
        public string Name { get; set; }
        public string NodeType { get; set; }
        public string Subnet { get; set; }
        public string NetType { get; set; }
        public Dictionary<string, string> Attributes { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>L'interfaccia come IO device: a quale sistema IO e con che numero.</summary>
    public sealed class HwIoConnector
    {
        public string IoSystem { get; set; }
        public int? IoSystemNumber { get; set; }
        public Dictionary<string, string> Attributes { get; set; } = new Dictionary<string, string>();
    }

    public sealed class HwSubnet
    {
        public string Name { get; set; }
        public string NetType { get; set; }
        public List<string> IoSystems { get; set; } = new List<string>();
    }

    /// <summary>Una tabella dei tag di un PLC: XML esportato (File) oppure i tag letti uno a uno (Tags).</summary>
    public sealed class TagTableFile
    {
        public string Plc { get; set; }
        public string GroupPath { get; set; }
        public string Name { get; set; }

        /// <summary>Percorso dell'XML relativo alla cartella di hardware.json.</summary>
        public string File { get; set; }
        public string State { get; set; }
        public string Reason { get; set; }
        public List<HwTag> Tags { get; set; } = new List<HwTag>();
    }

    public sealed class HwTag
    {
        public string Name { get; set; }
        public string DataType { get; set; }

        /// <summary>Indirizzo logico (%I0.0, %QW64, %MB10...), vuoto per i tag senza indirizzo.</summary>
        public string Address { get; set; }
        public string Comment { get; set; }
    }

    /// <summary>Esito del comando hardware.</summary>
    public sealed class HardwareResult
    {
        public string Hardware { get; set; }
        public int Devices { get; set; }
        public int Items { get; set; }
        public int Nodes { get; set; }
        public int TagTables { get; set; }
        public int Tags { get; set; }
        public bool Partial { get; set; }
        public int Warnings { get; set; }
    }
}
