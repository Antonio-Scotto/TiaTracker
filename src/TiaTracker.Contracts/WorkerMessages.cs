using System.Collections.Generic;

namespace TiaTracker.Contracts
{
    /// <summary>
    /// Una riga JSON su stdout del worker. Tutti i campi sono facoltativi
    /// tranne Type; il worker emette esattamente una riga "result" o "error"
    /// come ultima riga del protocollo.
    /// </summary>
    public sealed class WorkerLine
    {
        public const string TypeStatus = "status";
        public const string TypeProgress = "progress";
        public const string TypeItem = "item";
        public const string TypeWarn = "warn";
        public const string TypeResult = "result";
        public const string TypeError = "error";

        public string Type { get; set; }
        public string Code { get; set; }
        public string Text { get; set; }
        public string Phase { get; set; }
        public int? Done { get; set; }
        public int? Total { get; set; }
        public string Item { get; set; }
        public string Family { get; set; }
        public string State { get; set; }
        public int? ExitCode { get; set; }
        public string Command { get; set; }
        public object Data { get; set; }
    }

    /// <summary>Codici di stato noti (WorkerLine.Code con Type=status).</summary>
    public static class StatusCodes
    {
        public const string Attaching = "attaching";
        public const string WaitingAccessPrompt = "waiting-access-prompt";
        public const string Attached = "attached";
        public const string Opening = "opening";
        public const string Opened = "opened";
        public const string Collecting = "collecting";
        public const string Closing = "closing";
    }

    public sealed class HelloResult
    {
        public string WorkerVersion { get; set; }
        public int TiaMajor { get; set; }
        public string ApiPath { get; set; }
        public string EngineeringAssembly { get; set; }
        public string Framework { get; set; }
    }

    public sealed class InstanceInfo
    {
        public int Pid { get; set; }
        public string ProjectPath { get; set; }
        public string Mode { get; set; }
        public string AcquisitionTime { get; set; }
    }

    public sealed class InstancesResult
    {
        public List<InstanceInfo> Instances { get; set; } = new List<InstanceInfo>();
    }

    public sealed class PlcInfo
    {
        public string Name { get; set; }
        public string DeviceName { get; set; }
        public string OnlineState { get; set; }
        public int BlockCount { get; set; }
        public int TypeCount { get; set; }
    }

    public sealed class ProbeResult
    {
        public string ProjectName { get; set; }
        public string ProjectPath { get; set; }
        public bool? IsModified { get; set; }
        public string Source { get; set; }
        public List<PlcInfo> Plcs { get; set; } = new List<PlcInfo>();
    }

    public sealed class SnapshotResult
    {
        /// <summary>Un manifest per PLC: senza --plc il worker li fotografa tutti.</summary>
        public List<string> Manifests { get; set; } = new List<string>();
        public int Items { get; set; }
        public int Ok { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }
        public int Protected { get; set; }
        public int ChangedDuringExport { get; set; }
        public bool Partial { get; set; }

        /// <summary>hardware.json (dispositivi, rete, tag); null se saltato o fallito.</summary>
        public string Hardware { get; set; }
    }
}
