using System.Collections.ObjectModel;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using TiaTracker.App.Services;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Snapshots;

namespace TiaTracker.App.ViewModels;

public sealed class BlockItemRow
{
    public BlockItemRow(SnapshotItem item)
    {
        Item = item;
    }

    public SnapshotItem Item { get; }

    public string Name => Item.Name;

    public string Kind => Item.Kind;

    public string Group => Item.GroupPath;

    public string? Number => Item.Number?.ToString();

    public string? Language => Item.Language;

    public string State => Item.State == "ok" ? (Item.ChangedDuringExport ? "cambiato durante l'export" : "") : Item.State + ": " + Item.Reason;

    public string? CodeModified => Short(Item.CodeModifiedAttr);

    public string? Compiled => Short(Item.CompiledAttr);

    public bool HasScl => Item.SclRef != null;

    private static string? Short(string? iso) =>
        iso != null && DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime d)
            ? (d.Kind == DateTimeKind.Utc ? d.ToLocalTime() : d).ToString("dd/MM/yy HH:mm")
            : iso;
}

/// <summary>Scheda Blocchi: contenuto di uno snapshot, con filtri e anteprima del sorgente.</summary>
public sealed partial class BlocksViewModel : ObservableObject
{
    private readonly VersionDetailViewModel _owner;
    private List<BlockItemRow> _all = new();

    public BlocksViewModel(VersionDetailViewModel owner)
    {
        _owner = owner;
    }

    public ObservableCollection<SnapshotRow> Snapshots { get; } = new();

    [ObservableProperty]
    public partial SnapshotRow? Snapshot { get; set; }

    public ObservableCollection<BlockItemRow> Rows { get; } = new();

    [ObservableProperty]
    public partial BlockItemRow? Selected { get; set; }

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    public IReadOnlyList<string> Kinds { get; } = new[] { "Tutti", "FB", "FC", "OB", "GlobalDB", "InstanceDB", "ArrayDB", "UDT", "Non esportati" };

    [ObservableProperty]
    public partial string Kind { get; set; } = "Tutti";

    [ObservableProperty]
    public partial string Source { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowXml { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    partial void OnFilterChanged(string value) => Apply();

    partial void OnKindChanged(string value) => Apply();

    partial void OnSnapshotChanged(SnapshotRow? value) => LoadItems();

    partial void OnSelectedChanged(BlockItemRow? value) => LoadSource();

    partial void OnShowXmlChanged(bool value) => LoadSource();

    public void Reload()
    {
        long? keep = Snapshot?.Snapshot.Id;
        Snapshots.Clear();
        foreach (Snapshot s in AppServices.Snapshots.ByVersion(_owner.Version.Id))
        {
            Snapshots.Add(new SnapshotRow(s));
        }

        Snapshot = Snapshots.FirstOrDefault(s => s.Snapshot.Id == keep) ?? Snapshots.FirstOrDefault();
        if (Snapshot == null)
        {
            _all.Clear();
            Rows.Clear();
            Summary = "Nessuno snapshot: crearne uno dalla scheda Snapshot.";
        }
    }

    private void LoadItems()
    {
        _all = Snapshot == null
            ? new List<BlockItemRow>()
            : AppServices.Snapshots.Items(Snapshot.Snapshot.Id).Select(i => new BlockItemRow(i)).ToList();
        Summary = _all.Count == 0 ? "" :
            $"{_all.Count} elementi: {_all.Count(r => r.Item.Family == "block")} blocchi, {_all.Count(r => r.Item.Family == "type")} UDT, " +
            $"{_all.Count(r => r.Item.State != "ok")} non esportati, {_all.Count(r => r.HasScl)} con SCL";
        Apply();
    }

    private void Apply()
    {
        string f = Filter.Trim();
        Rows.Clear();
        foreach (BlockItemRow r in _all)
        {
            if (Kind == "Non esportati" ? r.Item.State == "ok" : Kind != "Tutti" && r.Kind != Kind)
            {
                continue;
            }

            if (f.Length > 0 && !r.Name.Contains(f, StringComparison.OrdinalIgnoreCase) && !r.Group.Contains(f, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Rows.Add(r);
        }
    }

    private void LoadSource()
    {
        if (Selected == null)
        {
            Source = "";
            return;
        }

        string? scl = ShowXml ? null : AppServices.Content.GetText(Selected.Item.SclRef);
        if (scl != null)
        {
            Source = scl;
            return;
        }

        string? xml = AppServices.Content.GetText(Selected.Item.XmlRef);
        if (xml == null)
        {
            Source = Selected.Item.Reason ?? "";
            return;
        }

        try
        {
            Source = XmlCanonicalizer.ToText(XDocument.Parse(xml));
        }
        catch (System.Xml.XmlException)
        {
            Source = xml;
        }
    }
}
