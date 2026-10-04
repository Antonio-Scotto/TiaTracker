using System.Globalization;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Scanning;

namespace TiaTracker.Core.Status;

/// <summary>Un badge accanto allo stato: testo, tono e spiegazione.</summary>
public sealed record StatusBadge(string Text, Tone Tone, string? ToolTip = null);

/// <summary>
/// Stato mostrato di una versione: quello scelto dall'utente (o Vecchia se e'
/// superata e non ne ha uno), i carichi correnti sui PLC e i badge calcolati
/// (aperta in TIA, non salvata, solo archivio, snapshot da aggiornare). I badge
/// non si salvano mai nel DB.
/// </summary>
public sealed record VersionStatusInfo(
    VersionState State,
    bool AutoOld,
    IReadOnlyList<VersionLoad> CurrentLoads,
    OpenState Open,
    bool ArchiveOnly,
    bool Unsaved,
    bool SnapshotStale,
    string ArchiveKinds = "",
    string? StateNote = null)
{
    public static VersionStatusInfo Empty { get; } =
        new(VersionState.None, false, Array.Empty<VersionLoad>(), OpenState.Closed, false, false, false);

    /// <summary>Lo stato da mostrare: quello scelto, oppure Vecchia se superata senza stato.</summary>
    public VersionState Effective => State != VersionState.None ? State : AutoOld ? VersionState.Vecchia : VersionState.None;

    public string ChipText => VersionStates.Italian(Effective) + (StateNote != null && State != VersionState.None ? " (" + StateNote + ")" : "");

    public Tone ChipTone => VersionStates.ToneOf(Effective);

    public bool OnPlc => CurrentLoads.Count > 0;

    /// <summary>Vecchie, archiviate e scartate escono dal lavoro corrente, salvo se sono ancora su un PLC.</summary>
    public bool IsRetired => VersionStates.IsRetired(Effective) && !OnPlc;

    public string LoadsText => string.Join(", ", CurrentLoads.Select(l =>
        l.PlcDisplay + " dal " + l.LoadedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)));

    public string ChipToolTip
    {
        get
        {
            string text = VersionStates.Description(Effective);
            if (AutoOld && State == VersionState.None)
            {
                text = "Vecchia in automatico: c'e' una versione piu' recente e questa non ha uno stato";
            }

            return OnPlc ? text + Environment.NewLine + "Sul PLC: " + LoadsText : text;
        }
    }

    public IEnumerable<StatusBadge> Badges
    {
        get
        {
            // Sul PLC ma con un altro stato (es. In lavoro con modifiche online): lo si dice a parte.
            if (OnPlc && Effective != VersionState.Caricata)
            {
                yield return new StatusBadge("Sul PLC", Tone.Success, LoadsText);
            }

            if (Open.Kind == OpenKind.Open)
            {
                yield return new StatusBadge("Aperta in TIA", Tone.Orange);
            }
            else if (Open.Kind == OpenKind.OpenOtherPc)
            {
                yield return new StatusBadge("Aperta da altro PC", Tone.Purple, Open.Describe());
            }
            else if (Open.Kind == OpenKind.StaleLock)
            {
                yield return new StatusBadge("Lock residuo", Tone.Warning, Open.Describe());
            }

            if (Unsaved)
            {
                yield return new StatusBadge("Non salvata", Tone.Danger, "L'ultimo snapshot da TIA aperto conteneva modifiche non salvate");
            }

            if (SnapshotStale)
            {
                yield return new StatusBadge("Snapshot da aggiornare", Tone.Warning, "Salvata in TIA dopo l'ultimo snapshot (o mai fotografata)");
            }

            if (ArchiveOnly)
            {
                yield return new StatusBadge(ArchiveKinds.Length > 0 ? "Solo archivio (" + ArchiveKinds + ")" : "Solo archivio", Tone.Muted);
            }
        }
    }
}

public static class VersionStatusCalculator
{
    /// <param name="currentLoads">Carichi correnti di questa versione.</param>
    /// <param name="lastAttachSnapshot">Ultimo snapshot da TIA aperto (per "Non salvata").</param>
    /// <param name="lastSnapshot">Ultimo snapshot di qualunque tipo (per "Snapshot da aggiornare").</param>
    public static VersionStatusInfo Compute(
        ProjectVersion version,
        IEnumerable<ProjectVersion> sameCommessa,
        IReadOnlyList<VersionLoad> currentLoads,
        OpenState open,
        Snapshot? lastAttachSnapshot,
        Snapshot? lastSnapshot = null)
    {
        bool superseded = version.State == VersionState.None && currentLoads.Count == 0 &&
                          sameCommessa.Any(o => o.Id != version.Id && !o.Missing &&
                                                string.CompareOrdinal(o.SortKey, version.SortKey) > 0);
        bool archiveOnly = !version.HasFolder && (version.HasRar || version.HasBackup);
        bool unsaved = lastAttachSnapshot is { ProjectModified: true } &&
                       (version.LastSavedUtc == null || lastAttachSnapshot.CreatedUtc > version.LastSavedUtc.Value);
        bool current = version.State is VersionState.InLavoro or VersionState.DaCaricare or VersionState.InCollaudo or VersionState.Caricata;
        bool stale = current && version.HasFolder && !version.Missing && version.LastSavedUtc != null &&
                     (lastSnapshot == null || lastSnapshot.CreatedUtc < version.LastSavedUtc.Value);
        return new VersionStatusInfo(version.State, superseded, currentLoads, open, archiveOnly, unsaved, stale,
            ArchiveKinds(version), version.StateNote);
    }

    /// <summary>"rar", "backup" o "rar + backup" per il badge Solo archivio.</summary>
    public static string ArchiveKinds(ProjectVersion v)
    {
        List<string> parts = new();
        if (v.HasRar)
        {
            parts.Add("rar");
        }

        if (v.HasBackup)
        {
            parts.Add("backup");
        }

        return string.Join(" + ", parts);
    }
}
