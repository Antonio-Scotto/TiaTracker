namespace TiaTracker.Core.Planning;

/// <summary>
/// Giorni lavorativi: dal lunedi' al venerdi' (o tutti, se la commessa lavora
/// nel fine settimana), meno le feste nazionali italiane (Pasquetta compresa).
/// Durate e ritardi delle dipendenze si contano in giorni lavorativi.
/// </summary>
public sealed class WorkCalendar
{
    private readonly Dictionary<int, HashSet<DateOnly>> _holidays = new();

    public WorkCalendar(bool workOnWeekends = false, bool italianHolidays = true)
    {
        WorkOnWeekends = workOnWeekends;
        ItalianHolidays = italianHolidays;
    }

    public static WorkCalendar Default { get; } = new();

    public bool WorkOnWeekends { get; }

    public bool ItalianHolidays { get; }

    public bool IsWeekend(DateOnly d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    public bool IsHoliday(DateOnly d) => ItalianHolidays && HolidaysOf(d.Year).Contains(d);

    public bool IsWorkingDay(DateOnly d) => (WorkOnWeekends || !IsWeekend(d)) && !IsHoliday(d);

    /// <summary>Il giorno stesso se lavorativo, altrimenti il primo lavorativo dopo.</summary>
    public DateOnly NextWorkingDay(DateOnly d)
    {
        while (!IsWorkingDay(d))
        {
            d = d.AddDays(1);
        }

        return d;
    }

    /// <summary>Il giorno stesso se lavorativo, altrimenti l'ultimo lavorativo prima.</summary>
    public DateOnly PreviousWorkingDay(DateOnly d)
    {
        while (!IsWorkingDay(d))
        {
            d = d.AddDays(-1);
        }

        return d;
    }

    /// <summary>Sposta di <paramref name="n"/> giorni lavorativi (anche negativi), contando solo i lavorativi.</summary>
    public DateOnly AddWorkingDays(DateOnly d, int n)
    {
        int step = Math.Sign(n);
        while (n != 0)
        {
            d = d.AddDays(step);
            if (IsWorkingDay(d))
            {
                n -= step;
            }
        }

        return d;
    }

    /// <summary>Giorni lavorativi da <paramref name="a"/> a <paramref name="b"/> compresi (0 se b &lt; a).</summary>
    public int WorkingDays(DateOnly a, DateOnly b)
    {
        int n = 0;
        for (DateOnly d = a; d <= b; d = d.AddDays(1))
        {
            if (IsWorkingDay(d))
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>Durata di un task in giorni lavorativi (almeno 1, anche se cade tutto in giorni festivi).</summary>
    public int Duration(DateOnly start, DateOnly end) => Math.Max(1, WorkingDays(start, end));

    /// <summary>Fine di un task che comincia in <paramref name="start"/> e dura <paramref name="duration"/> giorni lavorativi.</summary>
    public DateOnly EndFor(DateOnly start, int duration)
    {
        DateOnly first = NextWorkingDay(start);
        return duration <= 1 ? first : AddWorkingDays(first, duration - 1);
    }

    private HashSet<DateOnly> HolidaysOf(int year)
    {
        lock (_holidays)
        {
            if (!_holidays.TryGetValue(year, out HashSet<DateOnly>? set))
            {
                DateOnly easter = Easter(year);
                set = new HashSet<DateOnly>
                {
                    new(year, 1, 1), new(year, 1, 6), easter.AddDays(1), new(year, 4, 25), new(year, 5, 1), new(year, 6, 2),
                    new(year, 8, 15), new(year, 11, 1), new(year, 12, 8), new(year, 12, 25), new(year, 12, 26),
                };
                _holidays[year] = set;
            }

            return set;
        }
    }

    /// <summary>Domenica di Pasqua (calendario gregoriano, algoritmo di Meeus/Jones/Butcher).</summary>
    public static DateOnly Easter(int year)
    {
        int a = year % 19, b = year / 100, c = year % 100, d = b / 4, e = b % 4;
        int f = (b + 8) / 25, g = (b - f + 1) / 3, h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7, m = (a + 11 * h + 22 * l) / 451;
        int month = (h + l - 7 * m + 114) / 31, day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateOnly(year, month, day);
    }
}
