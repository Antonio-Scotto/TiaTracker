using TiaTracker.Contracts;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Hardware;
using TiaTracker.Core.Planning;

namespace TiaTracker.Core.Status;

/// <summary>Una versione sul PLC per la dashboard.</summary>
public sealed record PlcLoadCard(string Plc, long VersionId, string VersionLabel, DateTime LoadedUtc, bool HasProof, string? Note);

/// <summary>Una CPU del progetto: nome, tipo, codice, firmware, IP.</summary>
public sealed record CpuCard(string Name, string? Type, string? OrderNumber, string? Firmware, string? Ip);

/// <summary>Una riga delle attivita' recenti (versioni, modifiche, piano, snapshot, documenti).</summary>
public sealed record ActivityItem(DateTime Utc, string Category, string Title, string? Detail);

/// <summary>
/// Il quadro della commessa per la dashboard: cosa c'e' sui PLC, la versione su
/// cui si lavora, il progetto TIA (CPU, blocchi, hardware), le modifiche da
/// salvare, la pianificazione. Solo calcoli: i dati arrivano dal DB.
/// </summary>
public sealed class CommessaOverview
{
    public List<PlcLoadCard> Loads { get; } = new();
    public ProjectVersion? Work { get; init; }
    public DateTime? WorkSavedUtc { get; init; }
    public Snapshot? WorkSnapshot { get; init; }

    /// <summary>Il progetto e' stato salvato dopo l'ultimo snapshot: confronti e documenti sono indietro.</summary>
    public bool SnapshotStale { get; init; }
    public string? TiaVersion { get; init; }
    public List<CpuCard> Cpus { get; } = new();
    public HwSnapshot? Hardware { get; init; }
    public List<(string Label, int Count)> Blocks { get; } = new();
    public int BlocksTotal => Blocks.Sum(b => b.Count);
    public int Changes { get; init; }

    /// <summary>Modifiche con la versione di lavoro non ancora "Salvata" (o "Scartata").</summary>
    public int ChangesOpenOnWork { get; init; }
    public int Drafts { get; init; }
    public PlanKpi? Plan { get; init; }

    public static readonly IReadOnlyList<(string Kind, string Label)> BlockKinds = new[]
    {
        ("OB", "OB"), ("FB", "FB"), ("FC", "FC"), ("GlobalDB", "DB globali"), ("InstanceDB", "DB di istanza"), ("UDT", "UDT"),
    };

    public static CommessaOverview Build(
        IReadOnlyList<VersionLoad> currentLoads,
        IReadOnlyDictionary<long, ProjectVersion> versions,
        ProjectVersion? work,
        Snapshot? workSnapshot,
        IReadOnlyDictionary<string, int>? blockKinds,
        HwSnapshot? hardware,
        HardwareExport? hardwareData,
        IReadOnlyList<Change> changes,
        IReadOnlyList<PlanTask> tasks,
        WorkCalendar cal,
        DateOnly today)
    {
        DateTime? saved = work?.LastSavedUtc;
        CommessaOverview o = new()
        {
            Work = work,
            WorkSavedUtc = saved,
            WorkSnapshot = workSnapshot,
            SnapshotStale = work != null && saved != null && (workSnapshot == null || saved > workSnapshot.CreatedUtc.AddMinutes(1)),
            TiaVersion = work?.TiaVersionText ?? (hardwareData != null ? "V" + hardwareData.TiaMajor : null),
            Hardware = hardware,
            Changes = changes.Count(c => !c.Draft),
            Drafts = changes.Count(c => c.Draft),
            ChangesOpenOnWork = work == null ? 0 : changes.Count(c => !c.Draft && c.Versions.Any(v =>
                v.VersionId == work.Id && v.State is not (ChangeState.Saved or ChangeState.Dropped))),
            Plan = tasks.Count == 0 ? null : PlanKpi.Compute(tasks, today, cal),
        };

        foreach (VersionLoad l in currentLoads.OrderBy(l => l.Plc, StringComparer.OrdinalIgnoreCase))
        {
            string label = versions.TryGetValue(l.VersionId, out ProjectVersion? v) ? v.Label : "?";
            o.Loads.Add(new PlcLoadCard(l.PlcDisplay, l.VersionId, label, l.LoadedUtc, l.ProofSnapshotId != null, l.Note));
        }

        if (hardwareData != null)
        {
            foreach (HwModuleRow m in HardwareView.Modules(hardwareData).Where(m => m.IsCpu))
            {
                o.Cpus.Add(new CpuCard(m.Name, m.TypeName, m.OrderNumber, m.Firmware, m.Ip));
            }
        }

        if (blockKinds != null)
        {
            foreach ((string kind, string label) in BlockKinds)
            {
                if (blockKinds.TryGetValue(kind, out int n) && n > 0)
                {
                    o.Blocks.Add((label, n));
                }
            }
        }

        return o;
    }
}
