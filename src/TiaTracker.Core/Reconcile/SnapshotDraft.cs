using System.Globalization;
using System.Text;
using TiaTracker.Core.Compare;
using TiaTracker.Core.Domain;

namespace TiaTracker.Core.Reconcile;

/// <summary>Confronto automatico dello snapshot di un PLC con il suo precedente, gia' riconciliato.</summary>
public sealed record DetectedPlc(Snapshot Target, Snapshot Base, string BaseLabel, IReadOnlyList<ReconcileRow> Rows, long? RunId);

/// <summary>
/// Dopo ogni snapshot: i blocchi cambiati rispetto allo snapshot precedente che nessuna
/// modifica della versione dichiara diventano una bozza da convalidare a mano (titolo,
/// descrizione, motivazione). Una bozza per snapshot. Al giro dopo i suoi blocchi sono
/// dichiarati (le bozze contano), quindi non nascono doppioni.
/// </summary>
public static class SnapshotDraft
{
    /// <summary>Blocchi nel titolo; gli altri finiscono in "e altri N".</summary>
    private const int TitleBlocks = 3;

    /// <summary>
    /// Oltre questi blocchi non dichiarati la bozza non si crea: non e' una modifica ma un
    /// salto (cambio di versione di TIA, base sbagliata, progetto riorganizzato), da guardare
    /// nel Confronto. Una bozza con centinaia di blocchi andrebbe solo svuotata a mano.
    /// </summary>
    public const int MaxBlocks = 50;

    /// <summary>
    /// Base del confronto automatico: lo snapshot precedente della stessa versione e dello
    /// stesso PLC; se e' il primo della versione, il piu' recente della versione precedente
    /// piu' vicina. Null se non c'e' niente con cui confrontare.
    /// </summary>
    public static Snapshot? PickBase(Snapshot target, IReadOnlyList<Snapshot> commessaSnapshots, IReadOnlyDictionary<long, ProjectVersion> versions)
    {
        if (!versions.TryGetValue(target.VersionId, out ProjectVersion? version))
        {
            return null;
        }

        // Parziali ed export da cartella possono non avere tutti i blocchi: sembrerebbero eliminati.
        List<Snapshot> samePlc = commessaSnapshots
            .Where(s => s.Id != target.Id && IsComplete(s) && string.Equals(s.PlcName, target.PlcName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Snapshot? previous = samePlc
            .Where(s => s.VersionId == target.VersionId && s.CreatedUtc <= target.CreatedUtc)
            .OrderByDescending(s => s.CreatedUtc)
            .ThenByDescending(s => s.Id)
            .FirstOrDefault();
        if (previous != null || version.SortKey.Length == 0)
        {
            return previous;
        }

        return samePlc
            .Where(s => versions.TryGetValue(s.VersionId, out ProjectVersion? v) && v.SortKey.Length > 0 &&
                        string.CompareOrdinal(v.SortKey, version.SortKey) < 0)
            .OrderByDescending(s => versions[s.VersionId].SortKey, StringComparer.Ordinal)
            .ThenByDescending(s => s.CreatedUtc)
            .FirstOrDefault();
    }

    /// <summary>Snapshot con tutti i blocchi del PLC: solo questi entrano nel confronto automatico.</summary>
    public static bool IsComplete(Snapshot s) => !s.Partial && s.Source != SnapshotSources.Export;

    /// <summary>
    /// La bozza, o null se ogni blocco cambiato e' gia' dichiarato (o ignorato). I blocchi
    /// dichiarati da modifiche della versione di base, se diversa, o delle versioni fra la base
    /// e questa (senza snapshot: servono le versioni della commessa), non ci vanno: sono
    /// modifiche portate dalle versioni precedenti, non lavoro nuovo.
    /// </summary>
    public static Change? Build(long commessaId, ProjectVersion version, IReadOnlyList<DetectedPlc> plcs,
        IReadOnlyList<Change> changes, DateTime nowLocal, IReadOnlyDictionary<long, ProjectVersion>? versions = null)
    {
        List<(DetectedPlc Plc, ReconcileRow Row)> found = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (DetectedPlc plc in plcs)
        {
            HashSet<string> carried = CarriedFromBase(changes, version, plc.Base.VersionId, versions);
            List<ReconcileRow> undeclared = plc.Rows
                .Where(r => r.Status == ReconcileStatus.Undeclared && r.Entry != null && !carried.Contains(r.BlockName) &&
                            !(r.Entry.OldName != null && carried.Contains(r.Entry.OldName)))
                .ToList();
            HashSet<string> names = new(undeclared.Select(r => r.BlockName), StringComparer.OrdinalIgnoreCase);
            foreach (ReconcileRow r in undeclared)
            {
                // IDB e DB derivati da un FB/UDT che va nella stessa bozza (o portato dalla base): sono una conseguenza.
                if (r.Entry!.ParentName != null && (names.Contains(r.Entry.ParentName) || carried.Contains(r.Entry.ParentName)))
                {
                    continue;
                }

                if (seen.Add(r.BlockName))
                {
                    found.Add((plc, r));
                }
            }
        }

        if (found.Count == 0)
        {
            return null;
        }

        DetectedPlc first = found[0].Plc;
        Change c = new()
        {
            CommessaId = commessaId,
            Title = Title(found.Select(f => f.Row.BlockName).ToList()),
            Description = Description(found),
            Date = DateOnly.FromDateTime(nowLocal),
            Scope = ChangeScope.Plc,
            Draft = true,
            Excerpt = Origin(version, plcs),
        };

        foreach ((DetectedPlc _, ReconcileRow r) in found)
        {
            c.Blocks.Add(new ChangeBlock
            {
                BlockName = r.BlockName,
                Family = r.Family,
                Action = ActionOf(r.Entry),
                Note = BlockNote(r.Entry!),
            });
        }

        bool unsaved = first.Target.Source == SnapshotSources.Attach && first.Target.ProjectModified == true;
        c.Versions.Add(new ChangeVersion
        {
            VersionId = version.Id,
            State = unsaved ? ChangeState.Compiled : ChangeState.Saved,
            Note = unsaved ? "rilevata nello snapshot da TIA aperto, progetto NON salvato" : "rilevata nello snapshot",
            EvidenceRunId = first.RunId,
        });
        return c;
    }

    /// <summary>Azione del blocco nella modifica, dal confronto.</summary>
    public static string? ActionOf(CompareEntry? e) => e?.Status switch
    {
        CompareStatus.Added => "nuovo",
        CompareStatus.Removed => "elimina",
        CompareStatus.Modified or CompareStatus.Renamed => "modifica",
        _ => null,
    };

    private static HashSet<string> CarriedFromBase(IReadOnlyList<Change> changes, ProjectVersion version, long baseVersionId,
        IReadOnlyDictionary<long, ProjectVersion>? versions)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        if (baseVersionId == version.Id)
        {
            return names;
        }

        // La base e le versioni intermedie senza snapshot: il progetto le contiene tutte.
        HashSet<long> earlier = new() { baseVersionId };
        if (versions != null && versions.TryGetValue(baseVersionId, out ProjectVersion? baseVersion) &&
            baseVersion.SortKey.Length > 0 && version.SortKey.Length > 0)
        {
            earlier.UnionWith(versions.Values
                .Where(v => v.SortKey.Length > 0 && string.CompareOrdinal(v.SortKey, baseVersion.SortKey) > 0 &&
                            string.CompareOrdinal(v.SortKey, version.SortKey) < 0)
                .Select(v => v.Id));
        }

        foreach (Change c in changes.Where(c => c.Versions.Any(v => earlier.Contains(v.VersionId) && v.State != ChangeState.Dropped)))
        {
            foreach (ChangeBlock b in c.Blocks)
            {
                names.Add(b.BlockName);
            }
        }

        return names;
    }

    private static string Title(List<string> names)
    {
        string list = string.Join(", ", names.Take(TitleBlocks));
        int more = names.Count - TitleBlocks;
        return (names.Count == 1 ? "Blocco cambiato: " : "Blocchi cambiati: ") + list + (more > 0 ? " e altri " + more : "");
    }

    private static string Description(List<(DetectedPlc Plc, ReconcileRow Row)> found)
    {
        StringBuilder sb = new();
        bool manyPlc = found.Select(f => f.Plc.Target.PlcName).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
        foreach ((DetectedPlc plc, ReconcileRow r) in found)
        {
            sb.Append("- ").Append(r.BlockName);
            if (manyPlc && plc.Target.PlcName != null)
            {
                sb.Append(" (").Append(plc.Target.PlcName).Append(')');
            }

            sb.Append(": ").AppendLine(What(r.Entry!));
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>"modificato: codice, interfaccia; rete 3 Titolo (codice)".</summary>
    private static string What(CompareEntry e)
    {
        string what = e.StatusText.TrimEnd('?').ToLowerInvariant();
        if (e.OldName != null)
        {
            what += " da " + e.OldName;
        }

        if (e.Moved && e.Status != CompareStatus.Moved)
        {
            what += ", spostato di gruppo";
        }

        string note = BlockNote(e) ?? "";
        return note.Length > 0 ? what + ": " + note : what;
    }

    private static string? BlockNote(CompareEntry e)
    {
        List<string> parts = new();
        if (e.Facets != Facets.None)
        {
            parts.Add(e.FacetsText.ToLowerInvariant());
        }

        if (e.Units.Count > 0)
        {
            parts.Add(string.Join(", ", e.Units.Select(u => "rete " + u.Index + (string.IsNullOrWhiteSpace(u.Title) ? "" : " " + u.Title) + " (" + u.What + ")")));
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static string Origin(ProjectVersion version, IReadOnlyList<DetectedPlc> plcs)
    {
        StringBuilder sb = new("Rilevata in automatico dallo snapshot di " + version.Label);
        foreach (DetectedPlc p in plcs)
        {
            sb.AppendLine().Append("- ").Append(p.Target.PlcName ?? "PLC").Append(": snapshot del ").Append(Local(p.Target.CreatedUtc))
                .Append(" confrontato con ").Append(p.BaseLabel).Append(" del ").Append(Local(p.Base.CreatedUtc));
        }

        return sb.ToString();
    }

    private static string Local(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
}
