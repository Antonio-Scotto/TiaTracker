using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using TiaTracker.App.Services;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Scanning;
using TiaTracker.Core.Status;

namespace TiaTracker.App.ViewModels;

public sealed partial class CommessaNode : ObservableObject
{
    public CommessaNode(Commessa commessa)
    {
        Commessa = commessa;
    }

    [ObservableProperty]
    public partial Commessa Commessa { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Filtro dell'albero (MainViewModel.ApplyFilter).</summary>
    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    public ObservableCollection<VersionNode> Versions { get; } = new();

    public List<ScanRoot> Roots { get; private set; } = new();

    public string Title => Commessa.Display;

    /// <summary>Il nome accanto al codice (vuoto se coincide).</summary>
    public string NameText => string.IsNullOrWhiteSpace(Commessa.Name) || Commessa.Name == Commessa.Code ? "" : Commessa.Name;

    /// <summary>Carichi correnti sui PLC della commessa (aggiornati da Reload).</summary>
    public List<VersionLoad> CurrentLoads { get; private set; } = new();

    public string Subtitle
    {
        get
        {
            List<string> parts = new() { Versions.Count == 1 ? "1 versione" : Versions.Count + " versioni" };
            if (!string.IsNullOrWhiteSpace(Commessa.Customer))
            {
                parts.Insert(0, Commessa.Customer!);
            }

            VersionNode? working = Versions.FirstOrDefault(v => v.Status.State == VersionState.InLavoro);
            if (working != null)
            {
                parts.Add("in lavoro " + working.Label);
            }

            List<string> onPlc = Versions.Where(v => v.Status.OnPlc).Select(v => v.Label).ToList();
            if (onPlc.Count > 0)
            {
                parts.Add("sul PLC " + string.Join(", ", onPlc));
            }

            return string.Join(" · ", parts);
        }
    }

    partial void OnCommessaChanged(Commessa value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(NameText));
        OnPropertyChanged(nameof(Subtitle));
    }

    /// <summary>
    /// Ricarica versioni e stati dal DB e dal file system. I nodi esistenti
    /// vengono aggiornati sul posto, cosi' la selezione nell'albero resta.
    /// </summary>
    public void Reload(bool tiaRunning)
    {
        Roots = AppServices.Commesse.ScanRoots(Commessa.Id);
        List<ProjectVersion> versions = AppServices.Versions.ByCommessa(Commessa.Id);
        Dictionary<long, Snapshot> lastAttach = AppServices.Snapshots.LastAttachByVersion(Commessa.Id);
        Dictionary<long, Snapshot> latest = AppServices.Snapshots.LatestByVersion(Commessa.Id);
        CurrentLoads = AppServices.Versions.CurrentLoads(Commessa.Id);

        Dictionary<long, VersionNode> existing = Versions.ToDictionary(v => v.Version.Id);
        List<VersionNode> ordered = new();
        foreach (ProjectVersion v in versions)
        {
            ScanRoot? root = Roots.FirstOrDefault(r => r.Id == v.ScanRootId);
            OpenState open = ScanService.OpenStateOf(v, root, tiaRunning);
            lastAttach.TryGetValue(v.Id, out Snapshot? snap);
            latest.TryGetValue(v.Id, out Snapshot? last);
            List<VersionLoad> loads = CurrentLoads.Where(l => l.VersionId == v.Id).ToList();
            VersionStatusInfo status = VersionStatusCalculator.Compute(v, versions, loads, open, snap, last);

            if (!existing.TryGetValue(v.Id, out VersionNode? node))
            {
                node = new VersionNode(this, v, status);
            }
            else
            {
                node.Update(v, status);
            }

            ordered.Add(node);
        }

        for (int i = 0; i < ordered.Count; i++)
        {
            int at = Versions.IndexOf(ordered[i]);
            if (at < 0)
            {
                Versions.Insert(i, ordered[i]);
            }
            else if (at != i)
            {
                Versions.Move(at, i);
            }
        }

        while (Versions.Count > ordered.Count)
        {
            Versions.RemoveAt(Versions.Count - 1);
        }

        OnPropertyChanged(nameof(Subtitle));
    }

    /// <summary>Solo lo stato "aperta": file system, nessuna query.</summary>
    public void RefreshOpenStates(bool tiaRunning)
    {
        List<ProjectVersion> all = Versions.Select(v => v.Version).ToList();
        foreach (VersionNode node in Versions)
        {
            ScanRoot? root = Roots.FirstOrDefault(r => r.Id == node.Version.ScanRootId);
            OpenState open = ScanService.OpenStateOf(node.Version, root, tiaRunning);
            if (open != node.Status.Open)
            {
                node.Update(node.Version, node.Status with { Open = open });
            }
        }
    }

    public ScanRoot? RootOf(ProjectVersion v) => Roots.FirstOrDefault(r => r.Id == v.ScanRootId);

    /// <summary>Nome accessibile del TreeViewItem (UI Automation, lettori di schermo).</summary>
    public override string ToString() => Title;
}

public sealed partial class VersionNode : ObservableObject
{
    public VersionNode(CommessaNode parent, ProjectVersion version, VersionStatusInfo status)
    {
        Parent = parent;
        Version = version;
        Status = status;
    }

    public CommessaNode Parent { get; }

    [ObservableProperty]
    public partial ProjectVersion Version { get; set; }

    [ObservableProperty]
    public partial VersionStatusInfo Status { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Filtro dell'albero (MainViewModel.ApplyFilter).</summary>
    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    public string Label => Version.Label;

    /// <summary>Il chip dello stato, per il pallino colorato accanto all'etichetta.</summary>
    public ChipVm? MainChip => Status.Effective == VersionState.None ? null : new ChipVm(Status.ChipText, Status.ChipTone, Status.ChipToolTip);

    /// <summary>Vecchia, archiviata o scartata (e non su un PLC): nascosta col filtro "mostra vecchie" spento.</summary>
    public bool IsRetired => Status.IsRetired;

    public string Subtitle
    {
        get
        {
            List<string> parts = new();
            if (!string.IsNullOrEmpty(Version.TiaVersionText))
            {
                parts.Add(Version.TiaVersionText!);
            }
            else if (Version.TiaMajor.HasValue)
            {
                parts.Add("V" + Version.TiaMajor.Value);
            }

            if (Version.LastSavedUtc.HasValue)
            {
                parts.Add("salvata " + Version.LastSavedUtc.Value.ToLocalTime().ToString("dd/MM/yy HH:mm", CultureInfo.InvariantCulture));
            }
            else if (Version.LastBackupUtc.HasValue)
            {
                parts.Add("backup " + Version.LastBackupUtc.Value.ToLocalTime().ToString("dd/MM/yy", CultureInfo.InvariantCulture));
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Chip dello stato seguito dai badge calcolati.</summary>
    public IReadOnlyList<ChipVm> Chips
    {
        get
        {
            List<ChipVm> chips = new();
            if (MainChip != null)
            {
                chips.Add(MainChip);
            }

            foreach (StatusBadge badge in Status.Badges)
            {
                chips.Add(new ChipVm(badge.Text, badge.Tone, badge.ToolTip));
            }

            if (Version.Missing)
            {
                chips.Add(new ChipVm("Mancante", Tone.Danger, "La cartella non c'e' piu' nella cartella di scansione"));
            }

            return chips;
        }
    }

    public string ArchiveText => VersionStatusCalculator.ArchiveKinds(Version);

    public void Update(ProjectVersion version, VersionStatusInfo status)
    {
        Version = version;
        Status = status;
    }

    partial void OnVersionChanged(ProjectVersion value) => RaiseAll();

    partial void OnStatusChanged(VersionStatusInfo value) => RaiseAll();

    public override string ToString() => Parent.Commessa.Code + " " + Label;

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Chips));
        OnPropertyChanged(nameof(MainChip));
        OnPropertyChanged(nameof(IsRetired));
        OnPropertyChanged(nameof(ArchiveText));
    }
}
