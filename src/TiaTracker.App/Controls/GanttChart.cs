using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TiaTracker.App.Converters;
using TiaTracker.Core.Domain;
using TiaTracker.Core.Planning;

namespace TiaTracker.App.Controls;

/// <summary>Una barra del Gantt: riga, date, aspetto.</summary>
public sealed class GanttBar
{
    public long Id { get; init; }
    public int Row { get; init; }
    public DateOnly Start { get; init; }
    public DateOnly End { get; init; }
    public PlanKind Kind { get; init; }
    public int Progress { get; init; }
    public Tone Tone { get; init; } = Tone.Accent;
    public string Title { get; init; } = "";
    public string? ToolTip { get; init; }
    public bool Late { get; init; }
    public bool Cancelled { get; init; }

    /// <summary>Si sposta col mouse (non le fasi, non in sola lettura).</summary>
    public bool Draggable { get; init; } = true;
}

public sealed record GanttLink(long Id, long From, long To, bool Violated);

/// <summary>Un evento del progetto TIA sulla linea del tempo (versione creata, caricata sul PLC).</summary>
public sealed record GanttMarker(DateOnly Date, string Label, Tone Tone, string? ToolTip = null);

public sealed class GanttMoveEventArgs : EventArgs
{
    public GanttMoveEventArgs(long id, DateOnly start, DateOnly end)
    {
        Id = id;
        Start = start;
        End = end;
    }

    public long Id { get; }
    public DateOnly Start { get; }
    public DateOnly End { get; }
}

/// <summary>
/// Diagramma di Gantt disegnato in OnRender: intestazione mesi/settimane/giorni
/// secondo lo zoom e una fascia per le etichette dei marcatori TIA (cosi' non
/// coprono le barre), fine settimana e feste ombreggiati, linea di oggi, barre
/// con avanzamento, milestone a rombo, fasi, frecce fine-inizio.
/// Trascinando si sposta un task, dai bordi si cambiano inizio e fine; doppio
/// clic apre il task; Ctrl+rotella zoom, Maiusc+rotella scorre il tempo, Esc
/// annulla il trascinamento. Lo scorrimento verticale lo comanda la griglia.
/// </summary>
public sealed class GanttChart : FrameworkElement
{
    /// <summary>Come le righe della griglia accanto (le celle Fluent sono alte almeno 32).</summary>
    public const double RowHeight = 32;

    /// <summary>Mesi (0-24), giorni o settimane (24-48), etichette dei marcatori (48-72).</summary>
    public const double HeaderHeight = 72;
    private const double LaneTop = 48;
    private const double MinDayWidth = 3;
    private const double MaxDayWidth = 64;
    private const double Edge = 5;

    private static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

    public static readonly DependencyProperty BarsProperty = Register(nameof(Bars), typeof(IReadOnlyList<GanttBar>), null, OnDataChanged);
    public static readonly DependencyProperty LinksProperty = Register(nameof(Links), typeof(IReadOnlyList<GanttLink>), null);
    public static readonly DependencyProperty MarkersProperty = Register(nameof(Markers), typeof(IReadOnlyList<GanttMarker>), null, OnDataChanged);
    public static readonly DependencyProperty CalendarProperty = Register(nameof(Calendar), typeof(WorkCalendar), WorkCalendar.Default);
    public static readonly DependencyProperty SelectedIdProperty = Register(nameof(SelectedId), typeof(long?), null);
    public static readonly DependencyProperty DayWidthProperty = Register(nameof(DayWidth), typeof(double), 22.0, OnZoomChanged);
    public static readonly DependencyProperty VerticalOffsetProperty = Register(nameof(VerticalOffset), typeof(double), 0.0);
    public static readonly DependencyProperty ReadOnlyProperty = Register(nameof(ReadOnly), typeof(bool), false);

    private double _horizontalOffset;
    private DateOnly _origin;
    private DateOnly _rangeEnd;
    private bool _scrolledOnce;

    private DragMode _drag;
    private GanttBar? _dragBar;
    private double _dragAnchor;
    private DateOnly _dragStart;
    private DateOnly _dragEnd;
    private string? _hoverTip;

    /// <summary>Etichette dei marcatori disegnate nell'ultimo OnRender, per i suggerimenti.</summary>
    private readonly List<(Rect Area, string Tip)> _markerChips = new();

    public GanttChart()
    {
        Focusable = true;
        ClipToBounds = true;
        DateOnly today = Today;
        _origin = Monday(today.AddDays(-14));
        _rangeEnd = today.AddDays(60);
        System.Windows.Controls.ToolTipService.SetInitialShowDelay(this, 350);
    }

    private enum DragMode
    {
        None,
        Move,
        ResizeStart,
        ResizeEnd,
    }

    public IReadOnlyList<GanttBar>? Bars
    {
        get => (IReadOnlyList<GanttBar>?)GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    public IReadOnlyList<GanttLink>? Links
    {
        get => (IReadOnlyList<GanttLink>?)GetValue(LinksProperty);
        set => SetValue(LinksProperty, value);
    }

    public IReadOnlyList<GanttMarker>? Markers
    {
        get => (IReadOnlyList<GanttMarker>?)GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    public WorkCalendar Calendar
    {
        get => (WorkCalendar)GetValue(CalendarProperty);
        set => SetValue(CalendarProperty, value);
    }

    public long? SelectedId
    {
        get => (long?)GetValue(SelectedIdProperty);
        set => SetValue(SelectedIdProperty, value);
    }

    /// <summary>Pixel per giorno (zoom).</summary>
    public double DayWidth
    {
        get => (double)GetValue(DayWidthProperty);
        set => SetValue(DayWidthProperty, Math.Clamp(value, MinDayWidth, MaxDayWidth));
    }

    /// <summary>Scorrimento verticale in pixel, lo stesso della griglia accanto.</summary>
    public double VerticalOffset
    {
        get => (double)GetValue(VerticalOffsetProperty);
        set => SetValue(VerticalOffsetProperty, value);
    }

    public bool ReadOnly
    {
        get => (bool)GetValue(ReadOnlyProperty);
        set => SetValue(ReadOnlyProperty, value);
    }

    public double HorizontalOffset
    {
        get => _horizontalOffset;
        set
        {
            if (Math.Abs(value - _horizontalOffset) >= 0.01)
            {
                SetOffset(value);
            }
        }
    }

    private void SetOffset(double value)
    {
        _horizontalOffset = Math.Clamp(value, 0, Math.Max(0, ContentWidth - ActualWidth));
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public double ContentWidth => (_rangeEnd.DayNumber - _origin.DayNumber + 1) * DayWidth;

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    /// <summary>Trascinamento finito: nuove date proposte (chi ascolta decide se applicarle).</summary>
    public event EventHandler<GanttMoveEventArgs>? BarMoved;

    public event EventHandler<long>? BarDoubleClick;

    /// <summary>Clic su una riga (id del task della riga, null = vuoto).</summary>
    public event EventHandler<long?>? RowClicked;

    /// <summary>Rotella senza modificatori: lo scorrimento verticale lo fa la griglia.</summary>
    public event EventHandler<double>? VerticalScrollRequested;

    /// <summary>Zoom o scorrimento orizzontale cambiati (per la barra di scorrimento).</summary>
    public event EventHandler? ViewChanged;

    public void ScrollToDate(DateOnly date, int daysBefore = 7) => SetOffset((date.DayNumber - _origin.DayNumber - daysBefore) * DayWidth);

    /// <summary>Zoom tenendo ferma la data sotto il mouse (o al centro).</summary>
    public void Zoom(double factor, double? aroundX = null)
    {
        double x = aroundX ?? ActualWidth / 2;
        double day = (_horizontalOffset + x) / DayWidth;
        DayWidth *= factor;
        SetOffset(day * DayWidth - x);
    }

    // ---------- coordinate ----------

    private double X(DateOnly d) => (d.DayNumber - _origin.DayNumber) * DayWidth - _horizontalOffset;

    private DateOnly DateAt(double x) => _origin.AddDays((int)Math.Floor((x + _horizontalOffset) / DayWidth));

    private double RowTop(int row) => HeaderHeight + row * RowHeight - VerticalOffset;

    private static DateOnly Monday(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    /// <summary>Intervallo disegnabile: due settimane prima del primo task, due mesi dopo l'ultimo (e sempre oggi).</summary>
    private void UpdateRange()
    {
        DateOnly today = Today;
        DateOnly min = today, max = today;
        foreach (GanttBar b in Bars ?? Array.Empty<GanttBar>())
        {
            min = b.Start < min ? b.Start : min;
            max = b.End > max ? b.End : max;
        }

        foreach (GanttMarker m in Markers ?? Array.Empty<GanttMarker>())
        {
            min = m.Date < min ? m.Date : min;
            max = m.Date > max ? m.Date : max;
        }

        DateOnly origin = Monday(min.AddDays(-14));
        if (origin != _origin)
        {
            // Stessa data sullo schermo anche se l'inizio dell'intervallo cambia.
            _horizontalOffset += (_origin.DayNumber - origin.DayNumber) * DayWidth;
            _origin = origin;
        }

        _rangeEnd = max.AddDays(60);
        _horizontalOffset = Math.Clamp(_horizontalOffset, 0, Math.Max(0, ContentWidth - ActualWidth));
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        GanttChart g = (GanttChart)d;
        g.UpdateRange();
        if (!g._scrolledOnce && g.ActualWidth > 0 && g.Bars is { Count: > 0 })
        {
            g._scrolledOnce = true;
            g.ScrollToDate(g.Bars.Min(b => b.Start) < Today ? Today : g.Bars.Min(b => b.Start));
        }

        g.ViewChanged?.Invoke(g, EventArgs.Empty);
    }

    private static void OnZoomChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((GanttChart)d).ViewChanged?.Invoke(d, EventArgs.Empty);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (!_scrolledOnce && ActualWidth > 0)
        {
            _scrolledOnce = Bars is { Count: > 0 };
            ScrollToDate(Bars is { Count: > 0 } && Bars.Min(b => b.Start) > Today ? Bars.Min(b => b.Start) : Today);
        }

        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------- disegno ----------

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private static Pen PenOf(Brush b, double w, bool dashed = false)
    {
        Pen p = new(b, w);
        if (dashed)
        {
            p.DashStyle = new DashStyle(new double[] { 3, 3 }, 0);
        }

        p.Freeze();
        return p;
    }

    private FormattedText Text(string s, double size, Brush brush, bool bold = false) =>
        new(s, It, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal,
            FontStretches.Normal), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        Brush grid = Res("GridLineBrush"), subtle = Res("SubtleFillBrush"), text = Res("TextBrush"), muted = Res("MutedBrush");
        Brush accent = Res("AccentBrush"), danger = Res("DangerBrush"), card = Res("CardBrush");
        Pen gridPen = PenOf(grid, 1);
        WorkCalendar cal = Calendar;
        DateOnly first = DateAt(0), last = DateAt(w);

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // per ricevere il mouse ovunque
        dc.PushClip(new RectangleGeometry(new Rect(0, HeaderHeight, w, Math.Max(0, h - HeaderHeight))));

        // Giorni non lavorativi e inizio settimana.
        for (DateOnly d = first; d <= last; d = d.AddDays(1))
        {
            double x = X(d);
            if (DayWidth >= 5 && !cal.IsWorkingDay(d))
            {
                dc.DrawRectangle(subtle, null, new Rect(x, HeaderHeight, DayWidth, h - HeaderHeight));
            }

            if (d.DayOfWeek == DayOfWeek.Monday && DayWidth >= 3)
            {
                dc.DrawLine(gridPen, new Point(Math.Round(x) + 0.5, HeaderHeight), new Point(Math.Round(x) + 0.5, h));
            }
        }

        IReadOnlyList<GanttBar> bars = Bars ?? Array.Empty<GanttBar>();
        int rows = bars.Count == 0 ? 0 : bars.Max(b => b.Row) + 1;

        // Riga selezionata e righe.
        GanttBar? selected = bars.FirstOrDefault(b => b.Id == SelectedId);
        if (selected != null)
        {
            dc.DrawRectangle(Res("SelectionBrush"), null, new Rect(0, RowTop(selected.Row), w, RowHeight));
        }

        for (int r = 1; r <= rows; r++)
        {
            double y = Math.Round(RowTop(r)) + 0.5;
            if (y > HeaderHeight && y < h)
            {
                dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
            }
        }

        // Marcatori TIA.
        foreach (GanttMarker m in Markers ?? Array.Empty<GanttMarker>())
        {
            double x = X(m.Date) + DayWidth / 2;
            if (x < -50 || x > w + 50)
            {
                continue;
            }

            Brush b = ToneBrushConverter.Lookup(m.Tone, "Solid");
            dc.DrawLine(PenOf(b, 1, dashed: true), new Point(x, HeaderHeight), new Point(x, h));
        }

        // Oggi.
        double todayX = X(Today) + DayWidth / 2;
        dc.DrawLine(PenOf(accent, 1.5), new Point(todayX, HeaderHeight), new Point(todayX, h));

        Dictionary<long, GanttBar> byId = new();
        foreach (GanttBar b in bars)
        {
            byId[b.Id] = b;
        }

        // Frecce delle dipendenze, sotto le barre.
        foreach (GanttLink l in Links ?? Array.Empty<GanttLink>())
        {
            if (byId.TryGetValue(l.From, out GanttBar? from) && byId.TryGetValue(l.To, out GanttBar? to))
            {
                DrawLink(dc, Current(from), Current(to), from.Row, to.Row, l.Violated ? danger : muted);
            }
        }

        // Barre.
        foreach (GanttBar b in bars)
        {
            (DateOnly s, DateOnly e) = Current(b);
            DrawBar(dc, b, s, e, text, muted, danger);
        }

        dc.Pop();
        DrawHeader(dc, w, first, last, card, text, muted, accent, gridPen);
        DrawMarkerLane(dc, w, accent);

        // Etichetta delle date durante il trascinamento.
        if (_drag != DragMode.None && _dragBar != null)
        {
            string label = _dragStart == _dragEnd
                ? _dragStart.ToString("ddd dd/MM", It)
                : _dragStart.ToString("ddd dd/MM", It) + " → " + _dragEnd.ToString("ddd dd/MM", It) + "  (" + cal.Duration(_dragStart, _dragEnd) + " gg)";
            FormattedText ft = Text(label, 11.5, Brushes.White, bold: true);
            double x = Math.Clamp(X(_dragStart), 4, Math.Max(4, w - ft.Width - 16));
            double y = Math.Max(HeaderHeight + 2, RowTop(_dragBar.Row) - ft.Height - 8);
            dc.DrawRoundedRectangle(accent, null, new Rect(x, y, ft.Width + 12, ft.Height + 4), 4, 4);
            dc.DrawText(ft, new Point(x + 6, y + 2));
        }
    }

    private (DateOnly Start, DateOnly End) Current(GanttBar b) =>
        _drag != DragMode.None && _dragBar?.Id == b.Id ? (_dragStart, _dragEnd) : (b.Start, b.End);

    private void DrawBar(DrawingContext dc, GanttBar b, DateOnly s, DateOnly e, Brush text, Brush muted, Brush danger)
    {
        double top = RowTop(b.Row);
        if (top + RowHeight < HeaderHeight || top > ActualHeight)
        {
            return;
        }

        double mid = top + RowHeight / 2;
        Brush solid = ToneBrushConverter.Lookup(b.Tone, "Solid");
        double x1 = X(s), x2 = X(e.AddDays(1));
        double labelX;
        switch (b.Kind)
        {
            case PlanKind.Milestone:
            {
                double cx = x1 + DayWidth / 2, r = 7;
                StreamGeometry g = new();
                using (StreamGeometryContext c = g.Open())
                {
                    c.BeginFigure(new Point(cx, mid - r), true, true);
                    c.LineTo(new Point(cx + r, mid), true, false);
                    c.LineTo(new Point(cx, mid + r), true, false);
                    c.LineTo(new Point(cx - r, mid), true, false);
                }

                g.Freeze();
                dc.DrawGeometry(solid, b.Late ? PenOf(danger, 1.5) : null, g);
                labelX = cx + r + 5;
                break;
            }

            case PlanKind.Phase:
            {
                double y = mid - 4;
                Rect bar = new(x1, y, Math.Max(2, x2 - x1), 6);
                dc.DrawRectangle(text, null, bar);
                if (b.Progress > 0)
                {
                    dc.DrawRectangle(solid, null, new Rect(x1, y, bar.Width * b.Progress / 100.0, 6));
                }

                foreach (double x in new[] { x1, x2 })
                {
                    StreamGeometry g = new();
                    using (StreamGeometryContext c = g.Open())
                    {
                        double ax = x == x1 ? x1 : x2;
                        c.BeginFigure(new Point(ax - 4, y + 6), true, true);
                        c.LineTo(new Point(ax + 4, y + 6), true, false);
                        c.LineTo(new Point(ax, y + 11), true, false);
                    }

                    g.Freeze();
                    dc.DrawGeometry(text, null, g);
                }

                labelX = x2 + 8;
                break;
            }

            default:
            {
                Rect bar = new(x1 + 1, mid - 7, Math.Max(3, x2 - x1 - 2), 14);
                dc.DrawRoundedRectangle(ToneBrushConverter.Lookup(b.Tone, "Bg"), PenOf(b.Late ? danger : solid, b.Late ? 1.5 : 1), bar, 3, 3);
                if (b.Progress > 0)
                {
                    dc.PushClip(new RectangleGeometry(bar, 3, 3));
                    dc.DrawRectangle(solid, null, new Rect(bar.X, bar.Y, bar.Width * Math.Min(100, b.Progress) / 100.0, bar.Height));
                    dc.Pop();
                }

                if (b.Cancelled)
                {
                    dc.DrawLine(PenOf(muted, 1), new Point(bar.Left, mid), new Point(bar.Right, mid));
                }

                labelX = bar.Right + 6;
                break;
            }
        }

        if (labelX < ActualWidth && b.Title.Length > 0)
        {
            FormattedText ft = Text(b.Title, 11.5, b.Cancelled ? muted : text, bold: b.Kind == PlanKind.Phase);
            ft.MaxTextWidth = Math.Max(20, Math.Min(320, ActualWidth - labelX - 4));
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(ft, new Point(labelX, mid - ft.Height / 2));
        }
    }

    private void DrawLink(DrawingContext dc, (DateOnly Start, DateOnly End) from, (DateOnly Start, DateOnly End) to, int fromRow, int toRow, Brush brush)
    {
        double x1 = X(from.End.AddDays(1)), y1 = RowTop(fromRow) + RowHeight / 2;
        double x2 = X(to.Start), y2 = RowTop(toRow) + RowHeight / 2;
        Pen pen = PenOf(brush, 1.2);
        StreamGeometry g = new();
        using (StreamGeometryContext c = g.Open())
        {
            c.BeginFigure(new Point(x1, y1), false, false);
            double out1 = x1 + 6;
            if (x2 - 6 >= out1)
            {
                c.LineTo(new Point(out1, y1), true, false);
                c.LineTo(new Point(out1, y2), true, false);
                c.LineTo(new Point(x2 - 1, y2), true, false);
            }
            else
            {
                // Il successore comincia prima: si gira attorno passando fra le due righe.
                double between = toRow > fromRow ? RowTop(toRow) : RowTop(fromRow);
                c.LineTo(new Point(out1, y1), true, false);
                c.LineTo(new Point(out1, between), true, false);
                c.LineTo(new Point(x2 - 8, between), true, false);
                c.LineTo(new Point(x2 - 8, y2), true, false);
                c.LineTo(new Point(x2 - 1, y2), true, false);
            }
        }

        g.Freeze();
        dc.DrawGeometry(null, pen, g);
        StreamGeometry head = new();
        using (StreamGeometryContext c = head.Open())
        {
            c.BeginFigure(new Point(x2, y2), true, true);
            c.LineTo(new Point(x2 - 6, y2 - 4), true, false);
            c.LineTo(new Point(x2 - 6, y2 + 4), true, false);
        }

        head.Freeze();
        dc.DrawGeometry(brush, null, head);
    }

    private void DrawHeader(DrawingContext dc, double w, DateOnly first, DateOnly last, Brush back, Brush text, Brush muted, Brush accent, Pen gridPen)
    {
        dc.DrawRectangle(back, null, new Rect(0, 0, w, HeaderHeight));
        dc.DrawLine(gridPen, new Point(0, HeaderHeight - 0.5), new Point(w, HeaderHeight - 0.5));
        dc.DrawLine(gridPen, new Point(0, LaneTop - 0.5), new Point(w, LaneTop - 0.5));
        dc.DrawLine(gridPen, new Point(0, 24.5), new Point(w, 24.5));
        DateOnly today = Today;

        // Banda alta: mesi (o anni, con lo zoom molto basso).
        DateOnly m = new(first.Year, first.Month, 1);
        while (m <= last)
        {
            DateOnly next = m.AddMonths(1);
            double x = X(m), xe = X(next);
            dc.DrawLine(gridPen, new Point(Math.Round(x) + 0.5, 0), new Point(Math.Round(x) + 0.5, 24));
            string label = DayWidth * 30 >= 90 ? It.TextInfo.ToTitleCase(m.ToString("MMMM yyyy", It)) : m.ToString("MMM yy", It);
            FormattedText ft = Text(label, 12, text, bold: true);
            double lx = Math.Max(x + 6, 6);
            if (lx + ft.Width < xe - 4 || x < 0)
            {
                dc.DrawText(ft, new Point(Math.Min(lx, xe - ft.Width - 6), 4));
            }

            m = next;
        }

        // Banda bassa: giorni, oppure settimane.
        if (DayWidth >= 16)
        {
            for (DateOnly d = first; d <= last; d = d.AddDays(1))
            {
                double x = X(d);
                string label = DayWidth >= 30 ? d.ToString("ddd", It)[..1].ToUpperInvariant() + " " + d.Day : d.Day.ToString(CultureInfo.InvariantCulture);
                bool isToday = d == today;
                FormattedText ft = Text(label, 11, isToday ? accent : Calendar.IsWorkingDay(d) ? text : muted, bold: isToday);
                dc.DrawText(ft, new Point(x + (DayWidth - ft.Width) / 2, 29));
            }
        }
        else
        {
            for (DateOnly d = Monday(first); d <= last; d = d.AddDays(7))
            {
                double x = X(d);
                dc.DrawLine(gridPen, new Point(Math.Round(x) + 0.5, 25), new Point(Math.Round(x) + 0.5, LaneTop));
                if (DayWidth * 7 >= 34)
                {
                    int week = ISOWeek.GetWeekOfYear(d.ToDateTime(TimeOnly.MinValue));
                    string label = DayWidth * 7 >= 64 ? "S" + week + " · " + d.Day + "/" + d.Month : "S" + week;
                    dc.DrawText(Text(label, 10.5, muted), new Point(x + 4, 30));
                }
            }
        }

        // Oggi nell'intestazione: freccia sotto i giorni, linea attraverso la fascia dei marcatori.
        double tx = X(today) + DayWidth / 2;
        StreamGeometry g = new();
        using (StreamGeometryContext c = g.Open())
        {
            c.BeginFigure(new Point(tx - 5, LaneTop - 6), true, true);
            c.LineTo(new Point(tx + 5, LaneTop - 6), true, false);
            c.LineTo(new Point(tx, LaneTop), true, false);
        }

        g.Freeze();
        dc.DrawGeometry(accent, null, g);
        dc.DrawLine(PenOf(accent, 1.5), new Point(tx, LaneTop), new Point(tx, HeaderHeight));
    }

    /// <summary>
    /// Etichette dei marcatori nella fascia sotto i giorni. Quelli dello stesso giorno
    /// diventano una sola etichetta; se la successiva e' troppo vicina l'etichetta si
    /// accorcia con i puntini, al limite resta un pallino. Il testo intero e' nel suggerimento.
    /// </summary>
    private void DrawMarkerLane(DrawingContext dc, double w, Brush accent)
    {
        _markerChips.Clear();
        var days = (Markers ?? Array.Empty<GanttMarker>())
            .GroupBy(m => m.Date)
            .OrderBy(g => g.Key)
            .Select(g => new
            {
                X = X(g.Key) + DayWidth / 2,
                Label = string.Join(" · ", g.Select(m => m.Label)),
                Tip = string.Join("\n", g.Select(m => m.ToolTip ?? m.Label)),
                Tone = g.Select(m => m.Tone).FirstOrDefault(t => t != Tone.Neutral, Tone.Neutral),
            })
            .ToList();

        const double chipHeight = 17;
        double chipTop = LaneTop + (HeaderHeight - LaneTop - chipHeight) / 2;
        dc.PushClip(new RectangleGeometry(new Rect(0, LaneTop, w, HeaderHeight - LaneTop - 1)));
        for (int i = 0; i < days.Count; i++)
        {
            double x = days[i].X;
            double next = i + 1 < days.Count ? days[i + 1].X : double.PositiveInfinity;
            if (next < 0 || x > w)
            {
                continue;
            }

            Brush solid = ToneBrushConverter.Lookup(days[i].Tone, "Solid");
            dc.DrawLine(PenOf(solid, 1, dashed: true), new Point(x, LaneTop), new Point(x, HeaderHeight));

            double room = next - x - 6;
            FormattedText ft = Text(days[i].Label, 10.5, Res("Tone." + days[i].Tone + ".Fg"));
            Rect area;
            if (room >= 34)
            {
                ft.MaxTextWidth = Math.Min(ft.Width + 1, room - 10);
                ft.MaxLineCount = 1;
                ft.Trimming = TextTrimming.CharacterEllipsis;
                area = new Rect(x + 3, chipTop, ft.Width + 10, chipHeight);
                dc.DrawRoundedRectangle(Res("Tone." + days[i].Tone + ".Bg"), null, area, 6, 6);
                dc.DrawText(ft, new Point(area.X + 5, chipTop + (chipHeight - ft.Height) / 2));
            }
            else
            {
                double cy = chipTop + chipHeight / 2;
                dc.DrawEllipse(solid, null, new Point(x, cy), 3.5, 3.5);
                area = new Rect(x - 6, chipTop, 12, chipHeight);
            }

            _markerChips.Add((area, days[i].Tip));
        }

        dc.Pop();
    }

    // ---------- mouse e tastiera ----------

    private (GanttBar? Bar, DragMode Mode) HitTest(Point p)
    {
        if (p.Y < HeaderHeight)
        {
            return (null, DragMode.None);
        }

        IReadOnlyList<GanttBar> bars = Bars ?? Array.Empty<GanttBar>();
        for (int i = bars.Count - 1; i >= 0; i--)
        {
            GanttBar b = bars[i];
            double top = RowTop(b.Row);
            if (p.Y < top || p.Y > top + RowHeight)
            {
                continue;
            }

            double x1 = X(b.Start), x2 = X(b.End.AddDays(1));
            if (b.Kind == PlanKind.Milestone)
            {
                double cx = x1 + DayWidth / 2;
                if (Math.Abs(p.X - cx) <= 9)
                {
                    return (b, DragMode.Move);
                }

                continue;
            }

            if (p.X >= x1 - Edge && p.X <= x2 + Edge)
            {
                if (b.Kind == PlanKind.Phase)
                {
                    return (b, DragMode.None);
                }

                if (x2 - x1 >= 3 * Edge && Math.Abs(p.X - x1) <= Edge)
                {
                    return (b, DragMode.ResizeStart);
                }

                if (Math.Abs(p.X - x2) <= Edge)
                {
                    return (b, DragMode.ResizeEnd);
                }

                return (b, DragMode.Move);
            }
        }

        return (null, DragMode.None);
    }

    private GanttBar? BarOfRow(double y)
    {
        int row = (int)Math.Floor((y - HeaderHeight + VerticalOffset) / RowHeight);
        return (Bars ?? Array.Empty<GanttBar>()).FirstOrDefault(b => b.Row == row);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        Point p = e.GetPosition(this);
        (GanttBar? bar, DragMode mode) = HitTest(p);
        GanttBar? rowBar = bar ?? (p.Y >= HeaderHeight ? BarOfRow(p.Y) : null);
        RowClicked?.Invoke(this, rowBar?.Id);
        if (bar == null)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            BarDoubleClick?.Invoke(this, bar.Id);
            e.Handled = true;
            return;
        }

        if (ReadOnly || !bar.Draggable || mode == DragMode.None)
        {
            return;
        }

        _drag = mode;
        _dragBar = bar;
        _dragAnchor = p.X;
        _dragStart = bar.Start;
        _dragEnd = bar.End;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point p = e.GetPosition(this);
        if (_drag != DragMode.None && _dragBar != null)
        {
            int delta = (int)Math.Round((p.X - _dragAnchor) / DayWidth);
            WorkCalendar cal = Calendar;
            GanttBar b = _dragBar;
            switch (_drag)
            {
                case DragMode.Move when b.Kind == PlanKind.Milestone:
                    _dragStart = _dragEnd = cal.NextWorkingDay(b.Start.AddDays(delta));
                    break;
                case DragMode.Move:
                    int duration = cal.Duration(b.Start, b.End);
                    _dragStart = cal.NextWorkingDay(b.Start.AddDays(delta));
                    _dragEnd = cal.EndFor(_dragStart, duration);
                    break;
                case DragMode.ResizeStart:
                    DateOnly s = b.Start.AddDays(delta);
                    _dragStart = s > b.End ? b.End : s;
                    break;
                case DragMode.ResizeEnd:
                    DateOnly en = b.End.AddDays(delta);
                    _dragEnd = en < b.Start ? b.Start : en;
                    break;
            }

            InvalidateVisual();
            return;
        }

        (GanttBar? bar, DragMode mode) = HitTest(p);
        Cursor = bar == null || ReadOnly || !bar.Draggable ? null
            : mode is DragMode.ResizeStart or DragMode.ResizeEnd ? Cursors.SizeWE
            : mode == DragMode.Move ? Cursors.SizeAll : null;

        string? tip = bar?.ToolTip ?? MarkerTip(p);
        if (tip != _hoverTip)
        {
            _hoverTip = tip;
            ToolTip = tip;
        }
    }

    private string? MarkerTip(Point p)
    {
        if (p.Y < HeaderHeight)
        {
            return p.Y < LaneTop ? null : _markerChips.Where(c => c.Area.Contains(p)).Select(c => c.Tip).FirstOrDefault();
        }

        foreach (GanttMarker m in Markers ?? Array.Empty<GanttMarker>())
        {
            if (Math.Abs(X(m.Date) + DayWidth / 2 - p.X) <= 4)
            {
                return m.ToolTip ?? m.Label;
            }
        }

        return null;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag == DragMode.None || _dragBar == null)
        {
            return;
        }

        GanttBar b = _dragBar;
        DateOnly s = _dragStart, en = _dragEnd;
        EndDrag();
        if (s != b.Start || en != b.End)
        {
            BarMoved?.Invoke(this, new GanttMoveEventArgs(b.Id, s, en));
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag != DragMode.None)
        {
            EndDrag();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _drag != DragMode.None)
        {
            EndDrag();
            e.Handled = true;
        }
    }

    private void EndDrag()
    {
        _drag = DragMode.None;
        _dragBar = null;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            Zoom(e.Delta > 0 ? 1.25 : 0.8, e.GetPosition(this).X);
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            HorizontalOffset -= e.Delta / 120.0 * DayWidth * 7;
        }
        else
        {
            VerticalScrollRequested?.Invoke(this, -e.Delta / 120.0 * RowHeight * 3);
        }

        e.Handled = true;
    }

    private static DependencyProperty Register(string name, Type type, object? defaultValue, PropertyChangedCallback? changed = null) =>
        DependencyProperty.Register(name, type, typeof(GanttChart),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender, changed));
}
