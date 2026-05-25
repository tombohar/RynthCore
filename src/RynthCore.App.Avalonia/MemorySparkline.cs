using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace RynthCore.App.Avalonia;

/// <summary>
/// Lightweight per-frame chart for the Memory tab. Custom-drawn in
/// <see cref="OnRender"/> — no Canvas full of Lines (too many allocations
/// per tick when the buffer hits 17k samples).
///
/// Series are passed in by the host on each <see cref="InvalidateAndRedraw"/>
/// call; this control keeps no sample storage of its own. The Y axis is
/// normalised per-series (each series has its own min/max) so we can overlay
/// MB, handle counts, and thread counts on the same plot without one drowning
/// out the others. The per-series stroke pattern is what the user reads.
///
/// Colorblind contract: line identity is carried by <see cref="MemorySeries.Dash"/>,
/// not by colour. The colour on each series is decorative and the legend
/// (rendered by the host as a separate StackPanel) reproduces the same dash
/// pattern next to the metric name so a user who can't tell the colours apart
/// can still match line to series.
/// </summary>
internal sealed class MemorySparkline : Control
{
    private List<MemorySeries> _series = new();
    private DateTime _windowStartUtc;
    private DateTime _windowEndUtc;
    private double? _hoverFractionX;
    // Drawn rectangles per series in last render — used by hover crosshair to
    // report the nearest sample value at the mouse-X position.
    private readonly List<RenderedSeries> _renderedSeries = new();
    // x86 LAA 4 GB ceiling, in MB. acclient.exe is flagged
    // IMAGE_FILE_LARGE_ADDRESS_AWARE, so on 64-bit Windows the 32-bit
    // process gets the full 4 GB user VM space, not the legacy 2 GB.
    // Drawn as a labelled guide line over the Virtual series whenever
    // Virtual is among the visible series.
    public const double X86CeilingMb = 4096.0;
    private static readonly IBrush PlotBackground = Brush.Parse("#0B1116");

    public MemorySparkline()
    {
        // Plain Control doesn't expose a Background property — we paint our
        // backdrop manually in Render. The host card already supplies padding;
        // we paint edge-to-edge inside our own bounds.
        Focusable = false;
        ClipToBounds = true;
    }

    public void UpdateData(IReadOnlyList<MemorySeries> series, DateTime windowStartUtc, DateTime windowEndUtc)
    {
        _series = new List<MemorySeries>(series);
        _windowStartUtc = windowStartUtc;
        _windowEndUtc = windowEndUtc;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point p = e.GetPosition(this);
        double w = Bounds.Width;
        _hoverFractionX = (w > 0 && p.X >= 0 && p.X <= w) ? (p.X / w) : null;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverFractionX = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        Rect bounds = new Rect(Bounds.Size);
        // Backdrop.
        context.FillRectangle(PlotBackground, bounds);

        // Axes inset — leave a band on the left for the Y label and a band on
        // the bottom for the time axis.
        double leftPad = 6;
        double rightPad = 8;
        double topPad = 8;
        double bottomPad = 22;
        double plotW = bounds.Width - leftPad - rightPad;
        double plotH = bounds.Height - topPad - bottomPad;
        if (plotW <= 8 || plotH <= 8)
            return;
        Rect plot = new Rect(leftPad, topPad, plotW, plotH);

        // Grid lines (3 horizontal, 3 vertical). Very faint so they don't
        // compete with the data lines.
        var gridPen = new Pen(Brush.Parse("#1E2A33"), 1);
        for (int i = 1; i <= 3; i++)
        {
            double y = plot.Y + plot.Height * (i / 4.0);
            context.DrawLine(gridPen, new Point(plot.X, y), new Point(plot.Right, y));
            double x = plot.X + plot.Width * (i / 4.0);
            context.DrawLine(gridPen, new Point(x, plot.Y), new Point(x, plot.Bottom));
        }

        // Axis frame.
        var framePen = new Pen(Brush.Parse("#33444F"), 1);
        context.DrawLine(framePen, new Point(plot.X, plot.Y), new Point(plot.X, plot.Bottom));
        context.DrawLine(framePen, new Point(plot.X, plot.Bottom), new Point(plot.Right, plot.Bottom));

        // X-axis labels: start time + end time.
        var labelFg = Brush.Parse("#73828D");
        var typeface = new Typeface(FontFamily.Default);
        var startLabel = new FormattedText(
            FormatAxisTime(_windowStartUtc),
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface, 10, labelFg);
        var endLabel = new FormattedText(
            FormatAxisTime(_windowEndUtc),
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface, 10, labelFg);
        context.DrawText(startLabel, new Point(plot.X, plot.Bottom + 4));
        context.DrawText(endLabel, new Point(plot.Right - endLabel.Width, plot.Bottom + 4));

        if (_series.Count == 0)
        {
            var empty = new FormattedText(
                "No metrics visible — toggle Show on a metric row.",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 12, labelFg);
            context.DrawText(empty, new Point(
                plot.X + (plot.Width - empty.Width) / 2,
                plot.Y + (plot.Height - empty.Height) / 2));
            return;
        }

        double windowSpanTicks = (_windowEndUtc - _windowStartUtc).Ticks;
        if (windowSpanTicks <= 0)
            return;

        _renderedSeries.Clear();

        // Render each visible series. Each series is normalised independently
        // (per-metric Y axis), so overlaying MB and Handles works visually.
        foreach (MemorySeries s in _series)
        {
            if (s.Points.Count < 2)
                continue;

            // Find min/max in window so the line uses the full vertical range.
            // Including the ceiling (for Virtual) keeps the line visually
            // anchored against it instead of stretching to fill the full pane.
            double min = double.MaxValue;
            double max = double.MinValue;
            foreach (MemoryPoint pt in s.Points)
            {
                if (pt.Time < _windowStartUtc || pt.Time > _windowEndUtc)
                    continue;
                if (pt.Value < min) min = pt.Value;
                if (pt.Value > max) max = pt.Value;
            }
            if (s.IncludeCeiling && X86CeilingMb > max)
                max = X86CeilingMb;
            if (min == double.MaxValue)
                continue;

            // Pad the range so a flat series isn't on the top/bottom edge.
            double range = max - min;
            if (range < 0.0001) { range = 1.0; min -= 0.5; }
            double pad = range * 0.08;
            min -= pad;
            max += pad;
            range = max - min;

            IDashStyle dashStyle = MakeDashStyle(s.Dash);
            // Slightly thicker stroke so dotted/dashed patterns remain visible
            // against the dark background.
            var pen = new Pen(s.Stroke, 2.0)
            {
                DashStyle = dashStyle,
                LineJoin = PenLineJoin.Round,
                LineCap = PenLineCap.Round
            };

            // 4 GB LAA ceiling line for Virtual.
            if (s.IncludeCeiling && X86CeilingMb >= min && X86CeilingMb <= max)
            {
                double yC = plot.Bottom - ((X86CeilingMb - min) / range) * plot.Height;
                var ceilingPen = new Pen(Brush.Parse("#C75450"), 1)
                {
                    DashStyle = new DashStyle(new double[] { 4.0, 3.0 }, 0)
                };
                context.DrawLine(ceilingPen, new Point(plot.X, yC), new Point(plot.Right, yC));
                var ceilLabel = new FormattedText(
                    "x86 LAA 4 GB ceiling",
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, 9,
                    Brush.Parse("#C75450"));
                context.DrawText(ceilLabel, new Point(plot.Right - ceilLabel.Width - 4, yC - ceilLabel.Height - 1));
            }

            var geom = new StreamGeometry();
            bool first = true;
            using (StreamGeometryContext ctx = geom.Open())
            {
                foreach (MemoryPoint pt in s.Points)
                {
                    if (pt.Time < _windowStartUtc || pt.Time > _windowEndUtc)
                        continue;
                    double fx = (pt.Time - _windowStartUtc).Ticks / windowSpanTicks;
                    double x = plot.X + fx * plot.Width;
                    double y = plot.Bottom - ((pt.Value - min) / range) * plot.Height;
                    if (first)
                    {
                        ctx.BeginFigure(new Point(x, y), false);
                        first = false;
                    }
                    else
                    {
                        ctx.LineTo(new Point(x, y));
                    }
                }
                ctx.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geom);

            _renderedSeries.Add(new RenderedSeries(s, min, range, plot));
        }

        // Hover crosshair + value tooltip at the mouse-X.
        if (_hoverFractionX.HasValue)
        {
            double hx = plot.X + _hoverFractionX.Value * plot.Width;
            var crosshairPen = new Pen(Brush.Parse("#26C1A6"), 1)
            {
                DashStyle = new DashStyle(new double[] { 2.0, 2.0 }, 0)
            };
            context.DrawLine(crosshairPen, new Point(hx, plot.Y), new Point(hx, plot.Bottom));

            // Snap hover to the nearest sample in the first rendered series.
            DateTime hoverTime = _windowStartUtc + TimeSpan.FromTicks(
                (long)(_hoverFractionX.Value * windowSpanTicks));

            // Build a "Time | series1: value | series2: value" tooltip block.
            var lines = new List<string> { hoverTime.ToLocalTime().ToString("HH:mm:ss") };
            foreach (RenderedSeries rs in _renderedSeries)
            {
                MemoryPoint? nearest = NearestPoint(rs.Series.Points, hoverTime);
                if (nearest is MemoryPoint pt)
                    lines.Add($"{rs.Series.Name}: {pt.Value:F1} {rs.Series.Unit}");
            }

            string tipText = string.Join("\n", lines);
            var tipBrush = Brush.Parse("#EAF0F4");
            var tipBg = Brush.Parse("#1A2E38");
            var tipBorder = new Pen(Brush.Parse("#26C1A6"), 1);
            var ft = new FormattedText(tipText,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 11, tipBrush);

            double boxW = ft.Width + 10;
            double boxH = ft.Height + 8;
            double boxX = hx + 8;
            if (boxX + boxW > plot.Right) boxX = hx - 8 - boxW;
            double boxY = plot.Y + 4;
            var box = new Rect(boxX, boxY, boxW, boxH);
            context.FillRectangle(tipBg, box, 4);
            context.DrawRectangle(null, tipBorder, box, 4, 4);
            context.DrawText(ft, new Point(boxX + 5, boxY + 4));
        }
    }

    private static MemoryPoint? NearestPoint(IReadOnlyList<MemoryPoint> pts, DateTime t)
    {
        if (pts.Count == 0)
            return null;
        // Linear scan is fine — even 17k samples is sub-ms at 60 Hz.
        long bestDiff = long.MaxValue;
        MemoryPoint best = pts[0];
        foreach (MemoryPoint p in pts)
        {
            long d = Math.Abs((p.Time - t).Ticks);
            if (d < bestDiff) { bestDiff = d; best = p; }
        }
        return best;
    }

    private static string FormatAxisTime(DateTime utc)
    {
        DateTime local = utc.ToLocalTime();
        return local.ToString("HH:mm:ss");
    }

    private static IDashStyle MakeDashStyle(DashPattern p) => p switch
    {
        DashPattern.Solid     => new DashStyle(),
        DashPattern.Dash      => DashStyle.Dash,
        DashPattern.Dot       => DashStyle.Dot,
        DashPattern.DashDot   => DashStyle.DashDot,
        DashPattern.LongDash  => new DashStyle(new double[] { 6.0, 2.0 }, 0),
        _                     => new DashStyle()
    };

    private readonly record struct RenderedSeries(MemorySeries Series, double Min, double Range, Rect Plot);
}

/// <summary>One series fed to the sparkline. <see cref="Dash"/> carries identity
/// for colorblind users; <see cref="Stroke"/> is decorative.</summary>
internal sealed class MemorySeries
{
    public string Name { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;
    public IBrush Stroke { get; init; } = Brushes.White;
    public DashPattern Dash { get; init; } = DashPattern.Solid;
    public bool IncludeCeiling { get; init; }
    public IReadOnlyList<MemoryPoint> Points { get; init; } = Array.Empty<MemoryPoint>();
}

internal readonly record struct MemoryPoint(DateTime Time, double Value);

internal enum DashPattern
{
    Solid,
    Dash,
    Dot,
    DashDot,
    LongDash
}
