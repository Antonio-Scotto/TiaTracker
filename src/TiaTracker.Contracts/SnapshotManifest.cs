using System.Collections.Generic;

namespace TiaTracker.Contracts
{
    /// <summary>
    /// manifest.json scritto dal worker accanto agli XML/SCL. Il Core lo legge
    /// per costruire lo snapshot: il worker non calcola hash ne' normalizza.
    /// </summary>
    public sealed class SnapshotManifest
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
        public bool? IsModifiedAtEnd { get; set; }
        public string PlcName { get; set; }
        public string DeviceName { get; set; }
        public string OnlineState { get; set; }
        public string StartedUtc { get; set; }
        public string FinishedUtc { get; set; }
        public bool Partial { get; set; }
        public bool WithScl { get; set; }
        public Dictionary<string, long> TimingsMs { get; set; } = new Dictionary<string, long>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<ManifestItem> Items { get; set; } = new List<ManifestItem>();
    }

    public static class ItemStates
    {
        public const string Ok = "ok";
        public const string Protected = "protected";
        public const string Skipped = "skipped";
        public const string Failed = "failed";
    }

    public static class Families
    {
        public const string Block = "block";
        public const string Type = "type";
    }

    public sealed class ManifestItem
    {
        public string Family { get; set; }
        public string Kind { get; set; }
        public string Name { get; set; }
        public string GroupPath { get; set; }
        public int? Number { get; set; }
        public string Language { get; set; }
        public string InstanceOf { get; set; }
        public string State { get; set; }
        public string Reason { get; set; }
        public string XmlFile { get; set; }
        public string SclFile { get; set; }
        public string SclReason { get; set; }
        public bool ChangedDuringExport { get; set; }
        public Dictionary<string, string> Attributes { get; set; } = new Dictionary<string, string>();
    }
}
