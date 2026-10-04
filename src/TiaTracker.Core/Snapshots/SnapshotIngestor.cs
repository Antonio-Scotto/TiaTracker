using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using TiaTracker.Contracts;
using TiaTracker.Core.Domain;

namespace TiaTracker.Core.Snapshots;

public sealed record IngestResult(Snapshot Snapshot, List<SnapshotItem> Items, List<string> Warnings, SnapshotManifest Manifest);

/// <summary>
/// Dal manifest.json del worker allo snapshot: legge XML e SCL, li mette nel
/// CAS, calcola gli hash sulla forma canonica. Tutto qui e non nel worker:
/// se l'algoritmo cambia si ricalcola dagli XML archiviati (Rehash).
/// </summary>
public static class SnapshotIngestor
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static SnapshotManifest ReadManifest(string path) =>
        JsonSerializer.Deserialize<SnapshotManifest>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException("manifest vuoto: " + path);

    /// <param name="sourceOverride">backup / rar quando il progetto aperto era estratto da un archivio.</param>
    public static IngestResult Ingest(
        string manifestPath,
        long versionId,
        IContentStore store,
        DateTime? projectSavedUtc,
        string? sourceOverride = null,
        string? sourceDetail = null)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        string manifestText = File.ReadAllText(manifestPath);
        SnapshotManifest manifest = JsonSerializer.Deserialize<SnapshotManifest>(manifestText, JsonOptions)
                                    ?? throw new InvalidDataException("manifest vuoto");
        List<string> warnings = new(manifest.Warnings ?? new List<string>());

        bool attach = manifest.Source == "attach";
        Snapshot snapshot = new()
        {
            VersionId = versionId,
            CreatedUtc = ParseUtc(manifest.FinishedUtc) ?? DateTime.UtcNow,
            Source = sourceOverride ?? (attach ? SnapshotSources.Attach : SnapshotSources.File),
            SourceDetail = sourceDetail ?? (attach ? "PID " + manifest.Pid : manifest.ProjectPath),
            ProjectPath = manifest.ProjectPath,
            PlcName = manifest.PlcName,

            // Un progetto appena aperto da file non e' mai "modificato": conta solo in attach.
            ProjectModified = attach ? manifest.IsModified : null,
            ProjectSavedUtc = projectSavedUtc,
            OnlineState = manifest.OnlineState,
            NormVersion = XmlCanonicalizer.NormVersion,
            WorkerVersion = manifest.WorkerVersion,
            TiaMajor = manifest.TiaMajor,
            Partial = manifest.Partial,
            ManifestHash = store.Put(manifestText, "manifest"),
            TimingsJson = JsonSerializer.Serialize(manifest.TimingsMs ?? new Dictionary<string, long>()),
        };

        List<SnapshotItem> items = new();
        foreach (ManifestItem m in manifest.Items ?? new List<ManifestItem>())
        {
            items.Add(IngestItem(dir, m, store, warnings));
        }

        snapshot.ItemCount = items.Count;
        snapshot.FailedCount = items.Count(i => i.State == ItemStates.Failed);
        return new IngestResult(snapshot, items, warnings, manifest);
    }

    private static SnapshotItem IngestItem(string dir, ManifestItem m, IContentStore store, List<string> warnings)
    {
        Dictionary<string, string> attrs = m.Attributes ?? new Dictionary<string, string>();
        SnapshotItem item = new()
        {
            Family = m.Family ?? Families.Block,
            Kind = m.Kind ?? "",
            Name = m.Name ?? "",
            GroupPath = m.GroupPath ?? "",
            Number = m.Number,
            Language = m.Language,
            InstanceOf = m.InstanceOf,
            State = m.State ?? ItemStates.Failed,
            Reason = m.Reason,
            AttrsJson = JsonSerializer.Serialize(new SortedDictionary<string, string>(attrs, StringComparer.Ordinal)),
            CodeModifiedAttr = Attr(attrs, "CodeModifiedDate") ?? Attr(attrs, "ModifiedDate"),
            InterfaceModifiedAttr = Attr(attrs, "InterfaceModifiedDate"),
            CompiledAttr = Attr(attrs, "CompileDate"),
            ChangedDuringExport = m.ChangedDuringExport,
        };

        if (m.XmlFile != null)
        {
            string path = Path.Combine(dir, m.XmlFile);
            try
            {
                string xml = StripBom(File.ReadAllText(path, Encoding.UTF8));
                string stored = XmlCanonicalizer.StripDocumentInfo(xml);
                item.XmlRef = store.Put(stored, "xml");
                ApplyHashes(item, stored);
            }
            catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
            {
                item.State = ItemStates.Failed;
                item.Reason = "XML illeggibile: " + ex.Message;
                warnings.Add(item.Name + ": " + item.Reason);
            }
        }
        else if (item.State == ItemStates.Ok)
        {
            item.State = ItemStates.Failed;
            item.Reason ??= "XML mancante";
        }

        if (m.SclFile != null)
        {
            string path = Path.Combine(dir, m.SclFile);
            try
            {
                item.SclRef = store.Put(StripBom(File.ReadAllText(path, Encoding.UTF8)), "scl");
            }
            catch (IOException ex)
            {
                warnings.Add(item.Name + ": SCL illeggibile: " + ex.Message);
            }
        }

        return item;
    }

    public static void ApplyHashes(SnapshotItem item, string storedXml)
    {
        XDocument canon = XmlCanonicalizer.Canonicalize(storedXml);
        BlockHashes h = Hasher.Compute(canon);
        item.HAll = h.All;
        item.HCode = h.Code;
        item.HIface = h.Interface;
        item.HInit = h.Init;
        item.HText = h.Text;
        item.HMeta = h.Meta;
        item.UnitsJson = Hasher.UnitsJson(h.Units);
    }

    /// <summary>Ricalcola gli hash dagli XML archiviati (dopo un cambio di NormVersion).</summary>
    public static int Rehash(IEnumerable<SnapshotItem> items, IContentStore store)
    {
        int n = 0;
        foreach (SnapshotItem item in items)
        {
            string? xml = store.GetText(item.XmlRef);
            if (xml == null)
            {
                continue;
            }

            ApplyHashes(item, xml);
            n++;
        }

        return n;
    }

    private static string? Attr(Dictionary<string, string> attrs, string name) =>
        attrs.TryGetValue(name, out string? v) && v.Length > 0 ? v : null;

    private static string StripBom(string s) => s.Length > 0 && s[0] == '﻿' ? s[1..] : s;

    private static DateTime? ParseUtc(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return null;
        }

        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime d)
            ? d
            : null;
    }
}
