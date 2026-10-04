using System.Globalization;

namespace TiaTracker.Core.Planning;

/// <summary>Una riga dello storico della pianificazione (resta anche dopo l'eliminazione del task).</summary>
public sealed class PlanEvent
{
    public long Id { get; set; }
    public long CommessaId { get; set; }
    public long? TaskId { get; set; }
    public string TaskTitle { get; set; } = "";

    /// <summary>Le righe di una stessa azione (un trascinamento con la sua cascata) hanno lo stesso lotto.</summary>
    public string? Batch { get; set; }
    public DateTime Utc { get; set; }
    public string? User { get; set; }
    public string Kind { get; set; } = "";

    /// <summary>Colonna del task ("status", "assignee"...) o "date" per inizio e fine insieme.</summary>
    public string? Field { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? Reason { get; set; }

    /// <summary>Date spostate dalla cascata di un altro task.</summary>
    public bool Cascade { get; set; }

    /// <summary>Lotto dell'annullamento che ha riportato indietro questa riga.</summary>
    public string? UndoneBy { get; set; }

    public bool IsDates => Field == PlanFields.Dates;

    /// <summary>Per le date: spostamento dell'inizio in giorni di calendario.</summary>
    public int? ShiftDays =>
        IsDates && PlanFields.TryParseRange(OldValue, out DateOnly os, out _) && PlanFields.TryParseRange(NewValue, out DateOnly ns, out _)
            ? ns.DayNumber - os.DayNumber
            : null;

    /// <summary>"+3", oppure "fine +2" se si e' spostata solo la fine (durata cambiata), "" altrimenti.</summary>
    public string ShiftText
    {
        get
        {
            if (!IsDates || !PlanFields.TryParseRange(OldValue, out DateOnly os, out DateOnly oe) ||
                !PlanFields.TryParseRange(NewValue, out DateOnly ns, out DateOnly ne))
            {
                return "";
            }

            int start = ns.DayNumber - os.DayNumber, end = ne.DayNumber - oe.DayNumber;
            return start != 0 ? Signed(start) : end != 0 ? "fine " + Signed(end) : "";
        }
    }

    private static string Signed(int d) => (d > 0 ? "+" : "") + d.ToString(CultureInfo.InvariantCulture);
}

public static class PlanEventKinds
{
    public const string Created = "creato";
    public const string Modified = "modificato";
    public const string Dates = "date";
    public const string Linked = "collegato";
    public const string Unlinked = "scollegato";
    public const string Deleted = "eliminato";
    public const string Undone = "annullato";
}

/// <summary>Campi del task nello storico: nome della colonna, etichetta, valore leggibile.</summary>
public static class PlanFields
{
    public const string Dates = "date";

    public static string Label(string? field) => field switch
    {
        "title" => "Titolo",
        "description" => "Descrizione",
        "status" => "Stato",
        "priority" => "Priorita'",
        "assignee" => "Assegnatario",
        "kind" => "Tipo",
        "parent_id" => "Fase",
        "locked" => "Data fissata",
        "progress" => "Avanzamento",
        "estimated_hours" => "Ore stimate",
        "actual_hours" => "Ore consuntivo",
        "version_id" => "Versione",
        Dates => "Date",
        "predecessor" => "Dipende da",
        null => "",
        _ => field,
    };

    /// <summary>Valore leggibile (stati in italiano, date dd/MM, si'/no).</summary>
    public static string? Display(string? field, string? value)
    {
        if (value == null)
        {
            return null;
        }

        return field switch
        {
            "status" => PlanStates.Italian(PlanStates.StatusFromDb(value)),
            "priority" => PlanStates.Italian(PlanStates.PriorityFromDb(value)),
            "kind" => PlanStates.Italian(PlanStates.KindFromDb(value)),
            "locked" => value == "1" ? "si'" : "no",
            "progress" => value + "%",
            Dates when TryParseRange(value, out DateOnly s, out DateOnly e) =>
                s == e ? s.ToString("dd/MM/yy", CultureInfo.InvariantCulture)
                    : s.ToString("dd/MM/yy", CultureInfo.InvariantCulture) + " - " + e.ToString("dd/MM/yy", CultureInfo.InvariantCulture),
            _ => value,
        };
    }

    public static string Range(DateOnly start, DateOnly end) =>
        start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".." + end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static bool TryParseRange(string? text, out DateOnly start, out DateOnly end)
    {
        start = end = default;
        string[] parts = (text ?? "").Split("..");
        return parts.Length == 2 &&
               DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out start) &&
               DateOnly.TryParseExact(parts[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out end);
    }
}
