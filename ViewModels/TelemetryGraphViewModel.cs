using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FluxMux.Avalonia.ViewModels;

/// <summary>
/// A single data point on the telemetry graph.
/// </summary>
public readonly record struct TelemetryPoint(double X, double Y, string Label, bool IsFailure);

/// <summary>
/// View model for the live telemetry graph panel. Supports plotting any time-series or curve:
/// context-fill stress test (fill% vs TTFT), launch progress, model-switch progress, chat
/// port-rule triggers, etc. Points are added incrementally as data arrives, and the graph
/// updates live.
/// </summary>
public partial class TelemetryGraphViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _xLabel = string.Empty;

    [ObservableProperty]
    private string _yLabel = string.Empty;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private string _pointsString = string.Empty;

    [ObservableProperty]
    private bool _hasPoints;

    /// <summary>
    /// True when the plot area should be visible: either the graph is actively being driven
    /// (a stress test is running, so the testing band / heartbeats are showing) OR there are
    /// data points to display. This is the correct visibility condition for the plot area, the
    /// axis titles, and the Y-axis labels/ticks.
    ///
    /// Why not just IsActive or just HasPoints:
    /// - IsActive alone: hides the plot after a test completes (Deactivate sets IsActive=false),
    ///   even though the data points should still be visible.
    /// - HasPoints alone: hides the plot DURING a test (no successful level has completed yet,
    ///   so HasPoints=false), even though the testing band / heartbeats are actively showing.
    /// - IsActive || HasPoints: shows the plot both during a test (active) and after a test
    ///   (has points), and hides it only when the graph is truly idle/empty.
    ///
    /// The "No telemetry" placeholder is the inverse: it shows only when IsPlotVisible=false.
    /// </summary>
    [ObservableProperty]
    private bool _isPlotVisible;

    /// <summary>
    /// Recomputes <see cref="IsPlotVisible"/> from the current IsActive and HasPoints.
    /// Called whenever either changes.
    /// </summary>
    private void UpdateIsPlotVisible()
    {
        IsPlotVisible = IsActive || HasPoints;
    }

    [ObservableProperty]
    private string _failurePointsString = string.Empty;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private Geometry? _lineGeometry;

    [ObservableProperty]
    private Geometry? _failureLineGeometry;

    /// <summary>
    /// The geometry for the bar-chart rendering of the main (success) series. Each data point
    /// becomes a filled rectangle (bar) centered on its X position, growing upward from the
    /// baseline. This replaces the stroked polyline, which suffered from non-uniform stroke
    /// thickness when the 100×100 geometry was stretched into a non-square plot area
    /// (Viewbox Stretch="Fill" scales X and Y by different factors, so a 2px stroke became
    /// ~6px wide but ~3.6px tall — the "distorted thickness" bug).
    ///
    /// Built from a path string (not a StreamGeometry) so it works in the test environment
    /// where Avalonia's IPlatformRenderInterface is unavailable.
    /// </summary>
    [ObservableProperty]
    private Geometry? _barGeometry;

    /// <summary>
    /// The geometry for the bar-chart rendering of the failure series. Same bar shape, but
    /// bound to the red failure path in the XAML.
    /// </summary>
    [ObservableProperty]
    private Geometry? _failureBarGeometry;

    /// <summary>
    /// The path string for the bar-chart rendering of the main (success) series. Exposed as a
    /// string (not just a Geometry) so tests can assert on the bar coordinates without needing
    /// Avalonia's IPlatformRenderInterface (which is unavailable in the test environment).
    /// </summary>
    [ObservableProperty]
    private string _barPathData = string.Empty;

    /// <summary>
    /// The path string for the bar-chart rendering of the failure series.
    /// </summary>
    [ObservableProperty]
    private string _failureBarPathData = string.Empty;

    /// <summary>
    /// The geometry for the "ghost" outline of the PREVIOUS stress-test run, shown dimmed behind
    /// the live bars so the user can compare this run's curve against the last one (a performance
    /// comparison between model profiles). Built from the same bar-path geometry as the live bars
    /// but rendered with a low-opacity outline in the XAML. Null/empty when there is no previous
    /// run to compare against.
    /// </summary>
    [ObservableProperty]
    private Geometry? _ghostBarGeometry;

    /// <summary>
    /// The path string for the ghost outline of the previous stress-test run. Exposed as a string
    /// (not just a Geometry) so tests can assert on it without needing Avalonia's
    /// IPlatformRenderInterface.
    /// </summary>
    [ObservableProperty]
    private string _ghostBarPathData = string.Empty;

    /// <summary>
    /// True when a ghost outline from a previous run is visible.
    /// </summary>
    [ObservableProperty]
    private bool _hasGhostBars;

    /// <summary>
    /// The dynamic fill levels (token targets) for the current stress test. Used to position
    /// the X-axis labels under the correct bar positions. Null when using the default levels.
    /// </summary>
    private int[]? _fillLevels;

    // X-axis labels: 6 labels (one per fill level 25/50/75/90/100/110), each centered under
    // its corresponding bar. The positions (XAxisPos1-6) are the screen-x coordinates (0-100)
    // of each bar, computed from the axis range. The code-behind uses these to position the
    // labels in a Canvas so they align with the bars.
    [ObservableProperty]
    private string _xAxisLabel1 = "0";

    [ObservableProperty]
    private string _xAxisLabel2 = "25";

    [ObservableProperty]
    private string _xAxisLabel3 = "50";

    [ObservableProperty]
    private string _xAxisLabel4 = "75";

    [ObservableProperty]
    private string _xAxisLabel5 = "90";

    [ObservableProperty]
    private string _xAxisLabel6 = "100";

    // X-axis labels: text + position for each fill level. The code-behind uses these to
    // position TextBlocks in a Canvas. Using an ObservableCollection allows the number of
    // labels to vary dynamically based on the number of fill levels (e.g., 4 for VRAM-only,
    // 16 for YaRN 1M context).
    public class XAxisLabelItem
    {
        public string Text { get; set; } = string.Empty;
        public double Position { get; set; }
        /// <summary>
        /// True when this label marks the right end of the X-axis (the axis maximum). The
        /// code-behind right-aligns this label so its right edge sits at the axis end instead of
        /// centering it under the tick — the axis-max label is the "end" label and reads best
        /// flush against the right edge of the plot.
        /// </summary>
        public bool IsAxisMax { get; set; }
    }

    [ObservableProperty]
    private ObservableCollection<XAxisLabelItem> _xAxisLabels = new();

    [ObservableProperty]
    private double _xAxisPos1;

    [ObservableProperty]
    private double _xAxisPos2;

    [ObservableProperty]
    private double _xAxisPos3;

    [ObservableProperty]
    private double _xAxisPos4;

    [ObservableProperty]
    private double _xAxisPos5;

    [ObservableProperty]
    private double _xAxisPos6;

    // Bar value labels: text shown at the top of each bar (e.g. "42.3s" for TTFT, "245" for tok/s).
    // The Y positions (BarValueY1-6) are in 0-100 screen space (0=top, 100=bottom), positioned
    // slightly above each bar's top. The code-behind uses these to position TextBlocks in a Canvas.
    [ObservableProperty]
    private string _barValueText1 = string.Empty;

    [ObservableProperty]
    private string _barValueText2 = string.Empty;

    [ObservableProperty]
    private string _barValueText3 = string.Empty;

    [ObservableProperty]
    private string _barValueText4 = string.Empty;

    [ObservableProperty]
    private string _barValueText5 = string.Empty;

    [ObservableProperty]
    private string _barValueText6 = string.Empty;

    [ObservableProperty]
    private double _barValueY1;

    [ObservableProperty]
    private double _barValueY2;

    [ObservableProperty]
    private double _barValueY3;

    [ObservableProperty]
    private double _barValueY4;

    [ObservableProperty]
    private double _barValueY5;

    [ObservableProperty]
    private double _barValueY6;

    // The X position (in 0-100 screen space) of each bar's CENTER. The code-behind uses these to
    // position the bar-value TextBlocks directly above each bar. These differ from XAxisPos1-6:
    // the axis tick positions are evenly spaced (0/20/40/60/80/100%), but the bars sit at the
    // actual data-point X values, which in the per-turn view are turn indices (1..N) spaced by the
    // padded axis range — not evenly. Positioning the value labels at XAxisPos would offset them
    // from the bars (the "labels drift off the bars" bug). BarValueX keeps each label centered on
    // its own bar.
    [ObservableProperty]
    private double _barValueX1;

    [ObservableProperty]
    private double _barValueX2;

    [ObservableProperty]
    private double _barValueX3;

    [ObservableProperty]
    private double _barValueX4;

    [ObservableProperty]
    private double _barValueX5;

    [ObservableProperty]
    private double _barValueX6;

    [ObservableProperty]
    private string _yAxisMin = "0";

    [ObservableProperty]
    private string _yAxis25 = "0.25";

    [ObservableProperty]
    private string _yAxis50 = "0.5";

    [ObservableProperty]
    private string _yAxis75 = "0.75";

    [ObservableProperty]
    private string _yAxisMax = "1";

    [ObservableProperty]
    private string _yAxisTooltip = string.Empty;

    [ObservableProperty]
    private bool _hasUnfilledRegion;

    [ObservableProperty]
    private Geometry? _unfilledRegionGeometry;

    /// <summary>
    /// The X position (in the 0-100 normalized space) of the dotted vertical line that marks
    /// the boundary of the untested region. Zero when no unfilled region is shown.
    /// </summary>
    [ObservableProperty]
    private double _unfilledRegionLineX;

    /// <summary>
    /// The geometry for the dotted vertical line at the boundary of the untested region.
    /// A vertical line from Y=0 to Y=100 at the boundary X position. Null when no unfilled
    /// region is shown.
    /// </summary>
    [ObservableProperty]
    private Geometry? _unfilledRegionLineGeometry;

    /// <summary>
    /// When set (non-null), the graph shows a progressive bar at this fill % to indicate
    /// the level currently being tested. The bar grows from the baseline as prefill progress
    /// is reported, replacing the old full-height "testing band" that was misleading (it
    /// implied a value was being established when no data had been obtained yet). Cleared
    /// (set to null) when the level completes and the real data point is plotted.
    /// </summary>
    [ObservableProperty]
    private int? _testingFillPercent;

    /// <summary>
    /// The path data for the progressive "testing" bar (a partial-height bar at the testing
    /// fill % that grows as prefill progress is reported). Computed from
    /// <see cref="TestingFillPercent"/>, <see cref="TestingBarProgress"/>, and the axis range.
    /// Empty when no level is being tested.
    /// </summary>
    [ObservableProperty]
    private string _testingBarPath = string.Empty;

    /// <summary>
    /// A short label shown near the testing bar (e.g. "waiting 12s").
    /// </summary>
    [ObservableProperty]
    private string _testingBandLabel = string.Empty;

    /// <summary>
    /// True when the testing bar is visible (a level is currently being tested).
    /// </summary>
    [ObservableProperty]
    private bool _isTestingBandVisible;

    /// <summary>
    /// The prefill progress (0.0 to 1.0) for the currently testing level. Drives the height
    /// of the progressive testing bar. Updated on each heartbeat.
    /// </summary>
    private double _testingBarProgress;

    public ObservableCollection<TelemetryPoint> Points { get; } = new();

    /// <summary>
    /// The live <c>IList&lt;Point&gt;</c> bound to the main <c>Polyline.Points</c> control.
    ///
    /// This is a STABLE <see cref="ObservableCollection{T}"/> that is mutated IN PLACE
    /// (Clear + Add) whenever the data changes — it is never replaced with a new instance.
    /// This matters because Avalonia's <c>PolylineGeometry</c> subscribes to
    /// <c>INotifyCollectionChanged</c> on the bound collection (via its
    /// <c>OnPointsChanged</c> handler) and calls <c>InvalidateGeometry()</c> when the
    /// collection changes. If we replaced the collection with a new <c>List&lt;Point&gt;</c>
    /// on every update, the geometry would keep pointing at the old (often empty) collection
    /// and the curve would never appear — which is exactly why the graph panel stayed blank
    /// while the stress test ran. Mutating the same collection in place makes the geometry
    /// rebuild and re-render on every update.
    /// </summary>
    public ObservableCollection<Point> PolylinePoints { get; } = new();

    /// <summary>
    /// The live <c>IList&lt;Point&gt;</c> for the failure markers (red polyline). Same
    /// in-place-mutation contract as <see cref="PolylinePoints"/>.
    /// </summary>
    public ObservableCollection<Point> FailurePolylinePoints { get; } = new();

    private double _minX;
    private double _maxX;
    private double _minY;
    private double _maxY;
    private bool _hasData;

    // --- Scroll mode (live per-turn view) ---
    // When enabled, the X axis is a sliding window of turn indices rather than a data range.
    // The most-recent turn sits at the right edge; as the eased scroll offset advances 0->1 the
    // whole window slides left by one slot, so new bars enter from the right and old bars (with
    // their turn numbers and value labels) exit off the left edge. This is the "smoothly
    // scrolling" per-turn view. The stress test does NOT use scroll mode — it sets a fixed axis
    // range (_hasAxisRange) and its bars are evenly spaced at the fill levels, so all of the
    // scroll-mode code paths below are skipped for it.
    private bool _scrollMode;
    private int _scrollWindow = 6; // number of bars visible in the sliding window (matches the 6 value-label slots)
    private double _scrollOffset; // eased 0->1 offset; advanced by ScrollTo each timer tick

    /// <summary>
    /// The UTC timestamp of the most recent <see cref="AddPoint"/> call. The code-behind uses this
    /// to compute the eased scroll offset (elapsed time since the last turn) in scroll mode.
    /// </summary>
    public DateTime LastPointAddedUtc { get; private set; } = DateTime.MinValue;

    /// <summary>True when the graph is in scroll mode (the live per-turn view).</summary>
    public bool IsScrollMode => _scrollMode;

    /// <summary>
    /// Enters scroll mode with the given window size (number of visible bars). Clears any fixed
    /// axis range so the data-range fallback is not used. Call this when the per-turn view starts.
    /// </summary>
    public void BeginScrollMode(int windowSize = 6)
    {
        _scrollMode = true;
        _scrollWindow = Math.Max(2, Math.Min(windowSize, 6)); // cap at 6 (the value-label slot count)
        _hasAxisRange = false;
        _scrollOffset = 0.0;
        DiagLog($"BeginScrollMode window={_scrollWindow}");
    }

    /// <summary>
    /// Exits scroll mode (e.g. when a stress test takes over the panel). Restores normal
    /// data-range / fixed-axis-range positioning.
    /// </summary>
    public void EndScrollMode()
    {
        _scrollMode = false;
        DiagLog("EndScrollMode");
    }

    /// <summary>
    /// Advances the scroll window to the given eased offset (0.0 = newest bar at the right edge,
    /// 1.0 = the window has slid one full slot left) and re-renders the bars, value labels, and
    /// turn-number axis labels at their new positions. Called by the code-behind timer each tick
    /// with the eased offset computed from elapsed time since the last turn. No-op when not in
    /// scroll mode (the stress test and any non-scroll series ignore it).
    /// </summary>
    public void ScrollTo(double offset01)
    {
        if (!_scrollMode || Points.Count == 0)
        {
            return;
        }
        _scrollOffset = Math.Max(0.0, Math.Min(1.0, offset01));
        UpdateGraphStrings();
    }

    // Fixed axis range. When set, X values are normalized against [axisMinX, axisMaxX] and Y
    // values against [0, axisMaxY] (TTFT always starts at 0). This keeps the axis labels honest
    // (showing the real fill levels, not interpolated data-range values) and lets the Y-axis
    // grow as higher TTFT readings arrive without ever shrinking.
    private double _axisMinX;
    private double _axisMaxX;
    private double _axisMinY;
    private double _axisMaxY;
    private bool _hasAxisRange;

    // Y-axis minimum offset. When set (> 0), the Y-axis starts at this value instead of 0.
    // Used for the live per-turn telemetry view where the user wants the Y-axis to start at
    // a non-zero baseline (e.g. 180 for tok/s, 200 for TTFT) to make small variations visible.
    private double _yAxisMinOffset;

    // Label headroom fraction. The fraction of the chart height reserved for the bar value
    // label above the tallest bar. When a bar's value label would be forced to overlap the
    // top of the chart (or the bar itself), the Y-axis rescales to ensure this headroom is
    // available. Computed by the code-behind from the label height and chart height.
    private double _labelHeadroomFraction;

    /// <summary>
    /// Sets a fixed axis range for the current series. X is normalized against
    /// <c>[xMin, xMax]</c> and Y against <c>[0, yMax]</c>. The Y max grows (never shrinks) as
    /// successful points with larger Y values arrive. Call this once when the series starts
    /// (e.g. <c>SetAxisRange(0, 110, 0, 120)</c> for the context-fill stress test).
    /// </summary>
    public void SetAxisRange(double xMin, double xMax, double yMin, double yMax)
    {
        _axisMinX = xMin;
        _axisMaxX = xMax;
        _axisMinY = yMin;
        _axisMaxY = yMax;
        _hasAxisRange = true;
        UpdateAxisLabels();
        DiagLog($"SetAxisRange x=[{xMin},{xMax}] y=[{yMin},{yMax}]");
    }

    /// <summary>
    /// Sets the Y-axis minimum offset for the live per-turn telemetry view. When set to a value
    /// greater than 0, the Y-axis starts at that value instead of 0, making small variations
    /// above the baseline more visible. Call this after <see cref="SetSeries"/> for the
    /// scroll-mode graphs (e.g. 180 for tok/s, 200 for TTFT).
    /// </summary>
    public void SetYAxisMinOffset(double offset)
    {
        _yAxisMinOffset = Math.Max(0.0, offset);
        UpdateAxisLabels();
        DiagLog($"SetYAxisMinOffset yMin={_yAxisMinOffset}");
    }

    /// <summary>
    /// Sets the label headroom fraction — the fraction of the chart height reserved for the
    /// bar value label above the tallest bar. When a bar's value label would be forced to
    /// overlap the top of the chart, the Y-axis rescales (to a round multiple of 10) to ensure
    /// this headroom is available. Computed by the code-behind from the label height and chart
    /// height. Call this whenever the chart size or label font changes.
    /// </summary>
    public void SetLabelHeadroomFraction(double fraction)
    {
        var clamped = Math.Clamp(fraction, 0.0, 0.5); // cap at 50% to avoid absurd rescaling
        if (Math.Abs(clamped - _labelHeadroomFraction) < 0.001)
        {
            return; // no meaningful change; avoid a redundant re-render
        }
        _labelHeadroomFraction = clamped;
        UpdateAxisLabels();
        DiagLog($"SetLabelHeadroomFraction headroom={_labelHeadroomFraction:F3}");
    }

    /// <summary>
    /// Sets the dynamic fill levels (token targets) for the X-axis labels. Call this before or
    /// after <see cref="SetAxisRange"/> so the labels align with the actual bar positions.
    /// </summary>
    public void SetFillLevels(int[]? levels)
    {
        _fillLevels = levels;
        if (_hasAxisRange)
        {
            UpdateAxisLabels();
        }
    }

    /// <summary>
    /// Sets the progress/results summary for the context-fill stress test, shown alongside the
    /// graph title. The title (set via <see cref="SetSeries"/>) carries the axis description
    /// (e.g. "Context Size → TTFT (s)"), so the summary here contains ONLY the progress and
    /// results in a compact form: "4/6 · max 109s @ 75K · failed @ 90K".
    /// Token counts use the K-suffix format (e.g. "400K" not "409,600 tokens") to keep the
    /// string short enough to fit beside the title without overlapping it.
    /// Omit the failure clause when all levels succeeded; say "aborted" when the user cancelled.
    /// </summary>
    public void SetStressTestSummary(
        int completedLevels,
        int totalLevels,
        double maxTtftSeconds,
        int maxTtftFillTokens,
        int? failedFillTokens,
        bool aborted)
    {
        var parts = new List<string>
        {
            $"{completedLevels}/{totalLevels}"
        };
        if (maxTtftSeconds > 0)
        {
            parts.Add($"max {maxTtftSeconds:0.#}s @ {FormatTokenLabel(maxTtftFillTokens)}");
        }
        if (aborted)
        {
            parts.Add($"aborted @ {FormatTokenLabel(failedFillTokens ?? 0)}");
        }
        else if (failedFillTokens is { } ff)
        {
            parts.Add($"failed @ {FormatTokenLabel(ff)}");
        }
        SummaryText = string.Join(" · ", parts);
        DiagLog($"SetStressTestSummary '{SummaryText}'");
    }

    /// <summary>
    /// Sets the dotted-line + hatched "untested region" overlay. A dotted vertical line marks
    /// the boundary at <paramref name="boundaryFillPercent"/> (the first FAILED fill level —
    /// where the test stopped), and a hatched fill extends from that line to the axis max,
    /// making it clear that the test stopped early and the higher fill levels were never run.
    ///
    /// The boundary is pushed to the RIGHT edge of the bar that sits at that fill level (the
    /// bars are 7 units wide in the 0-100 space, centered on their fill position), so the
    /// dotted line and hatch never overlap a completed data bar. This matters when the test is
    /// aborted mid-level: the in-progress level has no bar, but the previous level's bar sits
    /// just to the left of the boundary and would otherwise be crossed by the line. Pass
    /// <c>null</c> (or a value at/above the axis max) to hide the overlay when all levels ran.
    /// </summary>
    public void SetUnfilledRegion(double? boundaryFillPercent)
    {
        // Clear the overlay when: no axis range, no boundary (null), or the boundary is at/above
        // the axis max (all levels ran). The `is not { }` pattern matches non-null values, so
        // `boundaryFillPercent is not { } last` is false when null — we must explicitly handle
        // the null case to clear the overlay.
        if (!_hasAxisRange || boundaryFillPercent is null || boundaryFillPercent >= _axisMaxX - 0.5)
        {
            HasUnfilledRegion = false;
            UnfilledRegionGeometry = null;
            UnfilledRegionLineX = 0;
            UnfilledRegionLineGeometry = null;
            return;
        }

        var last = boundaryFillPercent.Value;
        // Map the boundary fill level into the 0-100 normalized X space; the hatch spans from
        // that X to the right edge (100) and the full Y height (0-100).
        var xRange = _axisMaxX - _axisMinX;
        var nx = xRange > 0 ? (last - _axisMinX) / xRange * 100.0 : 0.0;
        if (nx < 0) nx = 0;
        if (nx > 100) nx = 100;

        // Push the boundary to the right edge of the bar centered on this fill level (bars are
        // 7 units wide, so half-width is 3.5) so the line/hatch start just past the bar instead
        // of crossing it.
        const double barHalfWidth = 3.5;
        var lineX = Math.Min(100.0, nx + barHalfWidth);

        // The dotted vertical line is at the boundary X; the hatched fill extends from there
        // to the right edge. The XAML renders the line with a StrokeDashArray and the fill
        // with a hatch DrawingBrush, so no solid red rectangle is shown.
        UnfilledRegionLineX = lineX;
        var linePath = $"M{lineX.ToString("0.#", CultureInfo.InvariantCulture)},0 L{lineX.ToString("0.#", CultureInfo.InvariantCulture)},100";
        UnfilledRegionLineGeometry = TryParseGeometry(linePath);
        var rect = new Rect(lineX, 0, 100.0 - lineX, 100.0);
        UnfilledRegionGeometry = new RectangleGeometry(rect);
        HasUnfilledRegion = true;
        DiagLog($"SetUnfilledRegion boundary={last} nx={nx:0.#} lineX={lineX:0.#}");
    }

    /// <summary>
    /// Sets the "ghost" outline of a previous stress-test run so it can be compared against the
    /// live run. Each (fillPercent, value) pair becomes a dimmed bar at the same X position as the
    /// live bars, using the same Y-axis mapping (so the two curves line up). The XAML renders this
    /// with a low-opacity outline behind the live bars. Pass <c>null</c> or an empty list to clear
    /// the ghost (e.g. when there is no previous run, or when the previous run was for a different
    /// model/variant and the comparison would be misleading).
    /// </summary>
    public void SetGhostBars(IEnumerable<(double Tokens, double Value)>? previousLevels)
    {
        if (previousLevels is null || !previousLevels.Any())
        {
            HasGhostBars = false;
            GhostBarGeometry = null;
            GhostBarPathData = string.Empty;
            return;
        }

        // Map each previous level into the 0-100 screen space using the SAME axis range as the
        // live bars (so the ghost aligns with the live curve). Y is linear against [0, axisMaxY]
        // when a fixed range is set (the stress test's case); otherwise fall back to the data range.
        var useAxis = _hasAxisRange;
        var axXRange = _axisMaxX - _axisMinX;
        var axYRange = _axisMaxY - _axisMinY;
        var xRange = _maxX - _minX;
        var yRange = _maxY - _minY;

        var ghostPoints = new List<Point>();
        foreach (var (tokens, value) in previousLevels)
        {
            double nx;
            if (useAxis)
            {
                nx = axXRange > 0 ? (tokens - _axisMinX) / axXRange * 100.0 : 50.0;
            }
            else
            {
                nx = xRange > 0 ? (tokens - _minX) / xRange * 100.0 : 50.0;
            }
            double ny;
            if (useAxis)
            {
                ny = axYRange > 0 ? (value - _axisMinY) / axYRange * 100.0 : 50.0;
            }
            else
            {
                ny = yRange > 0 ? (value - _minY) / yRange * 100.0 : 50.0;
            }
            var screenY = 100.0 - ny;
            ghostPoints.Add(new Point(nx, screenY));
        }

        var path = BuildGhostLines(ghostPoints);
        GhostBarPathData = path;
        GhostBarGeometry = TryParseGeometry(path);
        HasGhostBars = path.Length > 0;
        DiagLog($"SetGhostBars count={ghostPoints.Count} hasGhost={HasGhostBars}");
    }

    /// <summary>
    /// Starts a new series. Clears existing points and sets the axis labels.
    /// </summary>
    public void SetSeries(string title, string xLabel, string yLabel, string? yAxisTooltip = null)
    {
        Title = title;
        XLabel = xLabel;
        YLabel = yLabel;
        YAxisTooltip = yAxisTooltip ?? "TTFT = Time To First Token (latency from prompt to first token)";
        Points.Clear();
        _hasData = false;
        _minX = double.MaxValue;
        _maxX = double.MinValue;
        _minY = double.MaxValue;
        _maxY = double.MinValue;
        _hasAxisRange = false;
        _axisMinX = 0;
        _axisMaxX = 100;
        _axisMinY = 0;
        _axisMaxY = 1;
        _yAxisMinOffset = 0;
        _labelHeadroomFraction = 0;
        PointsString = string.Empty;
        FailurePointsString = string.Empty;
        PolylinePoints.Clear();
        FailurePolylinePoints.Clear();
        HasPoints = false;
        SummaryText = string.Empty;
        // Clear the bar value labels so they don't linger from a previous series (e.g. when
        // switching between stress test and telemetry modes).
        BarValueText1 = string.Empty;
        BarValueText2 = string.Empty;
        BarValueText3 = string.Empty;
        BarValueText4 = string.Empty;
        BarValueText5 = string.Empty;
        BarValueText6 = string.Empty;
        BarValueY1 = 0;
        BarValueY2 = 0;
        BarValueY3 = 0;
        BarValueY4 = 0;
        BarValueY5 = 0;
        BarValueY6 = 0;
        BarValueX1 = 0;
        BarValueX2 = 0;
        BarValueX3 = 0;
        BarValueX4 = 0;
        BarValueX5 = 0;
        BarValueX6 = 0;
        HasUnfilledRegion = false;
        UnfilledRegionGeometry = null;
        UnfilledRegionLineGeometry = null;
        UnfilledRegionLineX = 0;
        // Clear any stale testing band from a previous run so a new test starts clean.
        TestingFillPercent = null;
        TestingBarPath = string.Empty;
        TestingBandLabel = string.Empty;
        IsTestingBandVisible = false;
        _testingBarProgress = 0.0;
        // Clear ghost bars from a previous run.
        HasGhostBars = false;
        GhostBarGeometry = null;
        GhostBarPathData = string.Empty;
        IsActive = true;
        UpdateIsPlotVisible();
        DiagLog($"SetSeries title='{title}' x='{xLabel}' y='{yLabel}'");
    }

    private static void DiagLog(string message)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AI_Workbench", "Logs", "telemetry_graph_diag.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>
    /// Seeds the graph with a visible baseline so the panel shows a curve the moment the series
    /// starts, even before the first real data point arrives. A single <c>AddPoint</c> produces
    /// a zero-size figure (a point with no line), which is invisible; so this adds two points
    /// (0,0) and (1,0) — a flat baseline at the bottom of the plot. The first real level then
    /// extends the curve upward.
    /// </summary>
    public void SeedBaseline()
    {
        AddPoint(0, 0, label: "baseline", isFailure: false);
        AddPoint(1, 0, label: "baseline", isFailure: false);
    }

    /// <summary>
    /// Adds a data point to the current series. Updates the graph's polyline and markers.
    /// </summary>
    public void AddPoint(double x, double y, string label = "", bool isFailure = false)
    {
        var point = new TelemetryPoint(x, y, label, isFailure);
        Points.Add(point);
        LastPointAddedUtc = DateTime.UtcNow;

        if (!_hasData)
        {
            _minX = _maxX = x;
            _minY = _maxY = y;
            _hasData = true;
        }
        else
        {
            _minX = Math.Min(_minX, x);
            _maxX = Math.Max(_maxX, x);
            _minY = Math.Min(_minY, y);
            _maxY = Math.Max(_maxY, y);
        }

        // Auto-extend the axis range if the new point exceeds the current range. This ensures
        // the graph can accommodate new data points that go beyond the initial range (e.g., if
        // the context limit is higher than expected, or the test exceeds the initial range).
        if (_hasAxisRange)
        {
            var extended = false;
            if (x > _axisMaxX)
            {
                _axisMaxX = x * 1.05; // 5% padding
                extended = true;
            }
            if (y > _axisMaxY)
            {
                _axisMaxY = y * 1.25; // 25% padding to minimize re-scaling frequency
                extended = true;
            }
            if (extended)
            {
                UpdateAxisLabels();
                DiagLog($"AddPoint auto-extended axis to x=[{_axisMinX},{_axisMaxX}] y=[{_axisMinY},{_axisMaxY}]");
            }
        }

        UpdateGraphStrings();
        DiagLog($"AddPoint x={x} y={y} label='{label}' failure={isFailure} totalPoints={Points.Count}");
    }

    /// <summary>
    /// Updates the most recent point in place (or adds it if none exists yet). Used to show
    /// a live, moving point while a fill level is still in progress (e.g. waiting for the
    /// first token during a long prefill), so the graph builds in real time instead of
    /// staying empty. When the level completes, <see cref="AddPoint"/> finalizes it.
    /// </summary>
    public void UpdateLastPoint(double x, double y, string label = "", bool isFailure = false)
    {
        if (Points.Count == 0)
        {
            AddPoint(x, y, label, isFailure);
            return;
        }

        var last = Points[^1];
        Points[^1] = new TelemetryPoint(x, y, label, isFailure);

        // Re-derive the bounds to include the updated point.
        _minX = double.MaxValue;
        _maxX = double.MinValue;
        _minY = double.MaxValue;
        _maxY = double.MinValue;
        foreach (var p in Points)
        {
            _minX = Math.Min(_minX, p.X);
            _maxX = Math.Max(_maxX, p.X);
            _minY = Math.Min(_minY, p.Y);
            _maxY = Math.Max(_maxY, p.Y);
        }
        _hasData = true;

        // Auto-extend the axis range if the updated point exceeds the current range.
        if (_hasAxisRange)
        {
            var extended = false;
            if (x > _axisMaxX)
            {
                _axisMaxX = x * 1.05; // 5% padding
                extended = true;
            }
            if (y > _axisMaxY)
            {
                _axisMaxY = y * 1.25; // 25% padding to minimize re-scaling frequency
                extended = true;
            }
            if (extended)
            {
                UpdateAxisLabels();
                DiagLog($"UpdateLastPoint auto-extended axis to x=[{_axisMinX},{_axisMaxX}] y=[{_axisMinY},{_axisMaxY}]");
            }
        }

        UpdateGraphStrings();
        DiagLog($"UpdateLastPoint x={x} y={y} label='{label}' failure={isFailure} totalPoints={Points.Count}");
    }

    /// <summary>
    /// Clears the graph and hides it.
    /// </summary>
    public void Clear()
    {
        Points.Clear();
        _hasData = false;
        _hasAxisRange = false;
        PointsString = string.Empty;
        FailurePointsString = string.Empty;
        PolylinePoints.Clear();
        FailurePolylinePoints.Clear();
        HasPoints = false;
        SummaryText = string.Empty;
        // Clear the bar value labels so they don't linger from a previous series.
        BarValueText1 = string.Empty;
        BarValueText2 = string.Empty;
        BarValueText3 = string.Empty;
        BarValueText4 = string.Empty;
        BarValueText5 = string.Empty;
        BarValueText6 = string.Empty;
        BarValueY1 = 0;
        BarValueY2 = 0;
        BarValueY3 = 0;
        BarValueY4 = 0;
        BarValueY5 = 0;
        BarValueY6 = 0;
        BarValueX1 = 0;
        BarValueX2 = 0;
        BarValueX3 = 0;
        BarValueX4 = 0;
        BarValueX5 = 0;
        BarValueX6 = 0;
        HasUnfilledRegion = false;
        UnfilledRegionGeometry = null;
        UnfilledRegionLineGeometry = null;
        UnfilledRegionLineX = 0;
        IsActive = false;
        TestingFillPercent = null;
        TestingBarPath = string.Empty;
        TestingBandLabel = string.Empty;
        IsTestingBandVisible = false;
        _testingBarProgress = 0.0;
        UpdateIsPlotVisible();
    }

    /// <summary>
    /// Resets the IsActive flag to false without clearing the data points. Used after a stress
    /// test completes so the "No telemetry" placeholder can reappear when the graph is empty,
    /// while preserving the stress test results for the user to review.
    /// </summary>
    public void Deactivate()
    {
        IsActive = false;
        UpdateIsPlotVisible();
    }

    /// <summary>
    /// Re-derives the data bounds from the current <see cref="Points"/> and re-renders. Used after
    /// the live chat-turn view trims the oldest points to keep a rolling window: the axis bounds
    /// (<c>_minX</c>…<c>_maxY</c>) are recomputed from whatever remains, then the polyline/bars are
    /// rebuilt. Without this the graph would keep the stale bounds from the trimmed points.
    /// </summary>
    public void RefreshAfterTrim()
    {
        if (Points.Count == 0)
        {
            Clear();
            return;
        }

        _minX = double.MaxValue;
        _maxX = double.MinValue;
        _minY = double.MaxValue;
        _maxY = double.MinValue;
        foreach (var p in Points)
        {
            _minX = Math.Min(_minX, p.X);
            _maxX = Math.Max(_maxX, p.X);
            _minY = Math.Min(_minY, p.Y);
            _maxY = Math.Max(_maxY, p.Y);
        }

        _hasData = true;
        HasPoints = true;
        UpdateGraphStrings();
        DiagLog($"RefreshAfterTrim totalPoints={Points.Count} x=[{_minX},{_maxX}] y=[{_minY},{_maxY}]");
    }

    /// <summary>
    /// Shows a progressive bar at the given fill % to indicate the level currently being
    /// tested. The bar starts at the baseline (0% progress) and grows as
    /// <see cref="UpdateTestingBarProgress"/> is called on each heartbeat. This replaces the
    /// old full-height "testing band" that was misleading (it implied a value was being
    /// established when no data had been obtained yet). Call this in <c>onLevelStart</c>;
    /// call <see cref="ClearTestingBand"/> in <c>onLevelComplete</c> when the real data
    /// point is plotted.
    /// </summary>
    public void SetTestingBand(int fillPercent, string waitingLabel = "")
    {
        TestingFillPercent = fillPercent;
        TestingBandLabel = waitingLabel;
        IsTestingBandVisible = true;
        _testingBarProgress = 0.0;
        UpdateTestingBarPath();
        DiagLog($"SetTestingBand fill={fillPercent} label='{waitingLabel}'");
    }

    /// <summary>
    /// Updates the prefill progress (0.0 to 1.0) for the currently testing level, which drives
    /// the height of the progressive testing bar. Call this in <c>onLevelHeartbeat</c>.
    /// </summary>
    public void UpdateTestingBarProgress(double progress0to1)
    {
        if (TestingFillPercent is null) return;
        _testingBarProgress = Math.Clamp(progress0to1, 0.0, 1.0);
        UpdateTestingBarPath();
    }

    /// <summary>
    /// Updates the waiting label on the testing bar (e.g. "waiting 12s") without changing
    /// the bar position or height. Call this in <c>onLevelHeartbeat</c>.
    /// </summary>
    public void UpdateTestingBandLabel(string waitingLabel)
    {
        if (TestingFillPercent is null) return;
        TestingBandLabel = waitingLabel;
    }

    /// <summary>
    /// Hides the testing bar. Call this in <c>onLevelComplete</c> before plotting the real
    /// data point.
    /// </summary>
    public void ClearTestingBand()
    {
        TestingFillPercent = null;
        TestingBarPath = string.Empty;
        TestingBandLabel = string.Empty;
        IsTestingBandVisible = false;
        _testingBarProgress = 0.0;
        DiagLog("ClearTestingBand");
    }

    private void UpdateTestingBarPath()
    {
        if (TestingFillPercent is not { } fill)
        {
            TestingBarPath = string.Empty;
            return;
        }

        // Compute the X position in the 0-100 normalized coordinate space.
        double nx;
        if (_hasAxisRange)
        {
            var axXRange = _axisMaxX - _axisMinX;
            nx = axXRange > 0 ? (fill - _axisMinX) / axXRange * 100.0 : 50.0;
        }
        else
        {
            var xRange = _maxX - _minX;
            nx = xRange > 0 ? (fill - _minX) / xRange * 100.0 : 50.0;
        }

        // The progressive bar is the same width as the data bars (7 units in the 0-100 space),
        // centered on nx. Its height is driven by _testingBarProgress (0.0 to 1.0): at 0% the
        // bar is at the baseline (invisible), and it grows upward as prefill progress is
        // reported. This gives immediate visual activity at each bar position without the
        // misleading full-height background bar.
        const double barWidth = 7.0;
        const double baselineY = 100.0;
        var x0 = Math.Max(0, nx - barWidth / 2.0);
        var x1 = Math.Min(100, nx + barWidth / 2.0);
        // Minimum visible height so the bar appears the moment the level starts.
        var barHeight = Math.Max(1.0, _testingBarProgress * 99.0);
        var topY = baselineY - barHeight;
        TestingBarPath = $"M{x0.ToString("0.#", CultureInfo.InvariantCulture)},{topY.ToString("0.#", CultureInfo.InvariantCulture)} L{x1.ToString("0.#", CultureInfo.InvariantCulture)},{topY.ToString("0.#", CultureInfo.InvariantCulture)} L{x1.ToString("0.#", CultureInfo.InvariantCulture)},{baselineY.ToString("0.#", CultureInfo.InvariantCulture)} L{x0.ToString("0.#", CultureInfo.InvariantCulture)},{baselineY.ToString("0.#", CultureInfo.InvariantCulture)} Z";
    }

    private void UpdateGraphStrings()
    {
        if (Points.Count == 0)
        {
            PointsString = string.Empty;
            FailurePointsString = string.Empty;
            PolylinePoints.Clear();
            FailurePolylinePoints.Clear();
            HasPoints = false;
            UpdateIsPlotVisible();
            return;
        }

        var xRange = _maxX - _minX;
        var yRange = _maxY - _minY;

        // When a fixed axis range is set, normalize X against [axisMinX, axisMaxX] and Y against
        // [0, axisMaxY] (TTFT always starts at 0). This keeps the plotted curve aligned with the
        // honest axis labels (the real fill levels) instead of stretching to the data's own range.
        // The Y max grows (never shrinks) as successful points with larger Y values arrive.
        var useAxis = _hasAxisRange;
        var axMin = _axisMinX;
        var axMax = _axisMaxX;
        var axYMax = _axisMaxY;
        if (useAxis)
        {
            foreach (var p in Points)
            {
                if (!p.IsFailure && p.Y > axYMax)
                {
                    axYMax = p.Y;
                }
            }
            _axisMaxY = axYMax;
        }
        var axXRange = axMax - axMin;
        var axYRange = axYMax - _axisMinY;

        var successPoints = new StringBuilder();
        var failurePoints = new StringBuilder();
        // Build into fresh local lists, then push them into the STABLE bound collections
        // (Clear + Add) so the PolylineGeometry's INotifyCollectionChanged subscription fires
        // and the curve re-renders. Replacing the bound collection would leave the geometry
        // pointing at the old instance.
        var successList = new List<Point>();
        var failureList = new List<Point>();

        foreach (var p in Points)
        {
            double nx;
            if (_scrollMode)
            {
                // Scroll mode (live per-turn view): the X axis is a sliding window of turn indices.
                // p.X is the turn index. Bars are spaced 100/window units apart. The newest bar
                // (index _maxX) sits at the right edge (X=100) when the eased offset is 0, and
                // slides left by one slot as the offset advances to 1 — so new bars enter from the
                // right and old bars exit off the left edge. A bar with turn index i is at:
                //   nx = 100 - (newest - i + offset) * (100 / window)
                // i=newest, offset=0 -> 100 (right edge). i=newest-1, offset=0 -> 100 - slot.
                // i=newest, offset=1 -> 100 - slot (one slot left of the edge). Bars whose nx falls
                // outside [0,100] are clipped by the plot's ClipToBounds, so they exit cleanly.
                // Offset by half a bar width to the right so the leftmost bar's full width is
                // visible (its left edge sits at 0 instead of -3.5). The rightmost position is
                // usually empty, so clipping the newest bar on the right is acceptable.
                const double barWidthOffset = 3.5; // half of the 7.0-unit bar width
                var newest = _maxX; // the most recent turn index
                var slot = 100.0 / _scrollWindow;
                nx = 100.0 - (newest - p.X + _scrollOffset) * slot + barWidthOffset;
            }
            else if (useAxis)
            {
                nx = axXRange > 0 ? (p.X - axMin) / axXRange * 100.0 : 50.0;
            }
            else
            {
                nx = xRange > 0 ? (p.X - _minX) / xRange * 100.0 : 50.0;
            }
            double ny;
            if (useAxis)
            {
                // Y is always linear against [0, axisMaxY] when a fixed range is set (the stress
                // test's TTFT curve is steep but linear is honest).
                ny = axYRange > 0 ? (p.Y - _axisMinY) / axYRange * 100.0 : 50.0;
            }
            else
            {
                // Linear: use the effective Y-axis range (from GetEffectiveYAxisRange) so the
                // bars align with the Y-axis labels. The range is [yMin, yMax] where yMin is the
                // offset (or 0 if the offset >= yMax) and yMax is the rounded max.
                GetEffectiveYAxisRange(out var yMin, out var yMax);
                var ySpan = yMax - yMin;
                ny = ySpan > 0 ? (p.Y - yMin) / ySpan * 100.0 : 50.0;
                if (ny < 0) ny = 0; // clamp data below the offset to the bottom
            }

            var screenY = 100.0 - ny;
            var coord = $"{nx.ToString("0.#", CultureInfo.InvariantCulture)},{screenY.ToString("0.#", CultureInfo.InvariantCulture)}";

            if (p.IsFailure)
            {
                // Failure points (timeouts, HTTP errors) do NOT have a meaningful TTFT — plotting
                // them on the curve at TotalMs/1000 is misleading (an immediate HTTP 400 would
                // show as ~0s TTFT, implying superior performance). Instead, we mark the failure
                // with a distinct red X at the TOP of the chart (Y=0 in screen coords = max TTFT)
                // at the failure fill level, so it's clear the test stopped there without
                // implying a fast TTFT. The untested-region hatch (SetUnfilledRegion) covers the
                // rest.
                var failureX = nx;
                var failureY = 0.0; // top of the chart (max TTFT)
                if (failurePoints.Length > 0) failurePoints.Append(' ');
                failurePoints.Append($"{failureX.ToString("0.#", CultureInfo.InvariantCulture)},{failureY.ToString("0.#", CultureInfo.InvariantCulture)}");
                failureList.Add(new Point(failureX, failureY));
            }
            else
            {
                if (successPoints.Length > 0) successPoints.Append(' ');
                successPoints.Append(coord);
                successList.Add(new Point(nx, screenY));
            }
        }

        PointsString = successPoints.ToString();
        FailurePointsString = failurePoints.ToString();
        ReplaceCollectionInPlace(PolylinePoints, successList);
        ReplaceCollectionInPlace(FailurePolylinePoints, failureList);
        HasPoints = successList.Count > 0 || failureList.Count > 0;
        UpdateIsPlotVisible();

        // Build the Path geometries from the final point lists. The Viewbox in the XAML scales
        // the 100x100 geometry to fill the plot area, so no origin-prepending is needed.
        //
        // BARS (not a stroked polyline): each data point becomes a filled rectangle centered on
        // its X position, growing upward from the baseline. This avoids the stroke-thickness
        // distortion that a stroked polyline suffers when the 100x100 geometry is stretched into
        // a non-square plot area (Viewbox Stretch="Fill" scales X and Y by different factors).
        // A filled bar has no stroke — it's a solid rectangle that scales uniformly and looks
        // clean at any panel size.
        //
        // Built from path strings (not StreamGeometry) so they work in the test environment
        // where Avalonia's IPlatformRenderInterface is unavailable (StreamGeometry.Open()
        // throws there). The XAML Path control accepts a string and parses it to a Geometry.
        var barPath = BuildBarPath(successList);
        // Failure points are NOT bars — they are short horizontal dashed lines at the TOP of
        // the chart (Y=0 in screen coords = max TTFT) at the failure fill level. A full-height
        // red bar would imply the failure had a TTFT value, which is misleading. The dashed
        // line clearly marks "the test stopped here" without implying a performance value.
        var failureBarPath = BuildFailureLines(failureList);
        BarPathData = barPath;
        FailureBarPathData = failureBarPath;
        LineGeometry = TryParseGeometry(barPath);
        FailureLineGeometry = TryParseGeometry(failureBarPath);
        BarGeometry = TryParseGeometry(barPath);
        FailureBarGeometry = TryParseGeometry(failureBarPath);
        DiagLog($"UpdateGraphStrings success={successList.Count} failure={failureList.Count} " +
                $"barGeo={(BarGeometry != null ? "ok" : "NULL")} failGeo={(FailureBarGeometry != null ? "ok" : "NULL")} " +
                $"barPathLen={barPath.Length}");

        // Compute bar value labels: text + Y position for each bar's top.
        // The Y position is in 0-100 screen space, offset slightly above the bar's top.
        // The X position is the bar's CENTER (successList[i].X) — NOT the axis tick position.
        // The axis ticks are evenly spaced (0/20/40/60/80/100%), but the bars sit at the actual
        // data-point X values. In the per-turn view those are turn indices spaced by the padded
        // axis range, so using the axis-tick X would offset each label from its bar. Using the
        // bar center keeps every value label directly above its own bar. In the stress test the
        // bars are evenly spaced at the fill levels, so BarValueX == XAxisPos and this is a no-op
        // there (the stress test's alignment is unchanged).
        var labelOffset = 4.0; // 4% above the bar top
        var textValues = new[] { BarValueText1, BarValueText2, BarValueText3, BarValueText4, BarValueText5, BarValueText6 };
        var yValues = new[] { BarValueY1, BarValueY2, BarValueY3, BarValueY4, BarValueY5, BarValueY6 };
        var xValues = new[] { BarValueX1, BarValueX2, BarValueX3, BarValueX4, BarValueX5, BarValueX6 };
        // In scroll mode the visible window is the NEWEST _scrollWindow bars, so label those
        // (the last successList entries). In non-scroll mode label the first 6 (oldest first),
        // which is the existing behavior. This keeps the 6 value-label slots aligned with the
        // 6 turn-number axis labels in scroll mode.
        var visibleCount = Math.Min(6, successList.Count);
        var startIndex = _scrollMode ? successList.Count - visibleCount : 0;
        for (int i = 0; i < 6; i++)
        {
            var listIndex = startIndex + i;
            if (listIndex >= 0 && listIndex < successList.Count)
            {
                var p = successList[listIndex];
                // Find the success point at this list index to get the raw Y value for the label
                // text. successList is built in Points order (oldest first), so the i-th success
                // point in Points maps to successList[i] in non-scroll mode; in scroll mode we
                // offset by startIndex to reach the newest visible bars.
                var successIndex = 0;
                double rawY = 0;
                foreach (var pt in Points)
                {
                    if (!pt.IsFailure)
                    {
                        if (successIndex == listIndex)
                        {
                            rawY = pt.Y;
                            break;
                        }
                        successIndex++;
                    }
                }
                textValues[i] = FormatBarValueLabel(rawY);
                yValues[i] = Math.Max(0, p.Y - labelOffset);
                xValues[i] = p.X; // bar center in 0-100 screen space (the sliding window X)
            }
            else
            {
                textValues[i] = string.Empty;
                yValues[i] = 0;
                xValues[i] = 0;
            }
        }
        BarValueText1 = textValues[0];
        BarValueText2 = textValues[1];
        BarValueText3 = textValues[2];
        BarValueText4 = textValues[3];
        BarValueText5 = textValues[4];
        BarValueText6 = textValues[5];
        BarValueY1 = yValues[0];
        BarValueY2 = yValues[1];
        BarValueY3 = yValues[2];
        BarValueY4 = yValues[3];
        BarValueY5 = yValues[4];
        BarValueY6 = yValues[5];
        BarValueX1 = xValues[0];
        BarValueX2 = xValues[1];
        BarValueX3 = xValues[2];
        BarValueX4 = xValues[3];
        BarValueX5 = xValues[4];
        BarValueX6 = xValues[5];

        // Update the axis labels based on the actual data range.
        UpdateAxisLabels();

        var last = Points[^1];
        SummaryText = _hasData
            ? $"{FormatTokenLabel(_minX)}–{FormatTokenLabel(_maxX)} · last: {FormatTokenLabel(last.X)} → {last.Y:0.#} {YLabel}"
            : string.Empty;
        var coordLog = string.Join(" | ", successList.Select(p => $"({p.X:0.#},{p.Y:0.#})"));
        DiagLog($"UpdateGraphStrings successPts={successList.Count} failurePts={failureList.Count} hasPoints={HasPoints} polylineRef={PolylinePoints.GetHashCode()} coords=[{coordLog}]");
    }

    /// <summary>
    /// Replaces the contents of a stable <see cref="ObservableCollection{T}"/> with the items in
    /// <paramref name="newItems"/>, mutating it in place (Clear + Add) rather than swapping the
    /// instance. This keeps the bound <c>Polyline.Points</c> reference stable so the
    /// <c>PolylineGeometry</c>'s collection-changed subscription fires and the curve re-renders.
    /// </summary>
    private static void ReplaceCollectionInPlace(ObservableCollection<Point> target, List<Point> newItems)
    {
        target.Clear();
        foreach (var pt in newItems)
        {
            target.Add(pt);
        }
    }

    /// <summary>
    /// Updates the axis labels. When a fixed axis range is set (the context-fill stress test),
    /// the X-axis shows up to 6 labels (one per fill level), each centered under
    /// its corresponding bar. The positions are computed from the axis range so the labels align
    /// with the bars. The Y-axis shows 0/25/50/75/100% of the max TTFT in seconds. When no
    /// fixed range is set, it falls back to the data's own range (generic time-series use) with
    /// 6 evenly-spaced labels.
    /// </summary>
    private void UpdateAxisLabels()
    {
        if (_scrollMode)
        {
            // Scroll mode (live per-turn view): the X-axis shows the TURN NUMBER of each visible
            // bar, positioned at the same screen-X as the bar so each number scrolls beneath its
            // own bar (and exits off the left edge with it). This replaces the evenly-spaced tick
            // labels, which in scroll mode would not track the sliding bars. The Y-axis still uses
            // the data-range fallback (computed below the X-axis block, shared with non-scroll).
            var newest = _maxX;
            var slot = 100.0 / _scrollWindow;
            XAxisLabels.Clear();
            // Iterate the visible turn indices from oldest (left) to newest (right) so the labels
            // line up left-to-right with the bars. The newest bar is at index _maxX; the oldest
            // visible one is _maxX - (_scrollWindow - 1).
            for (int k = _scrollWindow - 1; k >= 0; k--)
            {
                var turnIndex = newest - k;
                // Only label turns that actually have a plotted point (turns are 1-based and may
                // not be contiguous if a turn produced no tokens and was skipped).
                if (!Points.Any(pt => pt.X == turnIndex))
                {
                    continue;
                }
                var screenX = 100.0 - (newest - turnIndex + _scrollOffset) * slot + 3.5; // +3.5 = half bar width offset (matches bar positioning)
                // Skip labels that have scrolled fully off the left edge (or not yet entered).
                if (screenX < -5.0 || screenX > 105.0)
                {
                    continue;
                }
                XAxisLabels.Add(new XAxisLabelItem
                {
                    Text = ((int)turnIndex).ToString(CultureInfo.InvariantCulture),
                    Position = screenX
                });
            }
            // Keep the XAxisLabel1-6 props in sync (the XAML may bind to either).
            for (int i = 0; i < 6; i++)
            {
                if (i < XAxisLabels.Count)
                {
                    SetXAxisLabelProp(i, XAxisLabels[i].Text, XAxisLabels[i].Position);
                }
                else
                {
                    SetXAxisLabelProp(i, string.Empty, 0.0);
                }
            }
            UpdateYAxisDataRangeLabels();
            return;
        }

        if (_hasAxisRange)
        {
            // X-axis: place labels at the fill-level positions (which are evenly spaced, since the
            // fill levels are step, 2*step, 3*step...). This keeps the labels aligned with the bars
            // and at absolute token values (e.g. "64K", "128K"), never interpolated percentages.
            // A final label is always added at the axis max; if it would sit too close (< 10% of the
            // axis) to the last fill-level label, that fill-level label is dropped to avoid overlap.
            var axisXRange = _axisMaxX - _axisMinX;
            if (axisXRange > 0)
            {
                var fillLevels = _fillLevels != null && _fillLevels.Length > 0
                    ? _fillLevels.Select(t => (double)t).ToArray()
                    : null;

                if (fillLevels != null && fillLevels.Length > 0)
                {
                    var labels = new List<(double Value, double ScreenPos, string Text)>();
                    int count = Math.Min(fillLevels.Length, 15);
                    for (int i = 0; i < count; i++)
                    {
                        var value = fillLevels[i];
                        var screenPos = (value - _axisMinX) / axisXRange * 100.0;
                        var text = FormatTokenLabel(value);
                        labels.Add((value, screenPos, text));
                    }

                    // Drop the last fill-level label if it is too close to the axis max to fit
                    // without overlapping the axis-max label that follows.
                    var lastFill = labels[^1];
                    var axisMaxPos = (_axisMaxX - _axisMinX) / axisXRange * 100.0;
                    if (axisMaxPos - lastFill.ScreenPos < 10.0)
                    {
                        labels.RemoveAt(labels.Count - 1);
                    }

                    // Always add a final label at the axis max so the right end is covered.
                    labels.Add((_axisMaxX, axisMaxPos, FormatTokenLabel(_axisMaxX)));

                    // Update the XAxisLabels collection with the new labels. The final label (the
                    // axis max, always the last entry) is flagged IsAxisMax so the code-behind can
                    // right-align it at the axis end.
                    XAxisLabels.Clear();
                    for (int li = 0; li < labels.Count; li++)
                    {
                        XAxisLabels.Add(new XAxisLabelItem
                        {
                            Text = labels[li].Text,
                            Position = labels[li].ScreenPos,
                            IsAxisMax = li == labels.Count - 1
                        });
                    }

                    // Also update the XAxisLabel1-6 and XAxisPos1-6 properties for backward
                    // compatibility (the XAML still binds to these for the first 6 labels).
                    for (int i = 0; i < 6; i++)
                    {
                        if (i < labels.Count)
                        {
                            switch (i)
                            {
                                case 0: XAxisLabel1 = labels[i].Text; XAxisPos1 = labels[i].ScreenPos; break;
                                case 1: XAxisLabel2 = labels[i].Text; XAxisPos2 = labels[i].ScreenPos; break;
                                case 2: XAxisLabel3 = labels[i].Text; XAxisPos3 = labels[i].ScreenPos; break;
                                case 3: XAxisLabel4 = labels[i].Text; XAxisPos4 = labels[i].ScreenPos; break;
                                case 4: XAxisLabel5 = labels[i].Text; XAxisPos5 = labels[i].ScreenPos; break;
                                case 5: XAxisLabel6 = labels[i].Text; XAxisPos6 = labels[i].ScreenPos; break;
                            }
                        }
                        else
                        {
                            switch (i)
                            {
                                case 0: XAxisLabel1 = ""; XAxisPos1 = 0; break;
                                case 1: XAxisLabel2 = ""; XAxisPos2 = 0; break;
                                case 2: XAxisLabel3 = ""; XAxisPos3 = 0; break;
                                case 3: XAxisLabel4 = ""; XAxisPos4 = 0; break;
                                case 4: XAxisLabel5 = ""; XAxisPos5 = 0; break;
                                case 5: XAxisLabel6 = ""; XAxisPos6 = 0; break;
                            }
                        }
                    }

                    // Log the label positions for debugging.
                    var labelsArray = labels.Select(l => l.Text).ToArray();
                    var positions = labels.Select(l => l.ScreenPos).ToArray();
                    DiagLog($"UpdateAxisLabels: axisXRange={axisXRange:F0} fillLevels=[{string.Join(",", fillLevels)}] labels=[{string.Join(",", labelsArray)}] positions=[{string.Join(",", positions.Select(p => p.ToString("F1")))}]");
                }
                else
                {
                    // No fill levels: use evenly-spaced labels (0/20/40/60/80/100% of the range).
                    XAxisLabel1 = FormatTokenLabel(_axisMinX);
                    XAxisLabel2 = FormatTokenLabel(_axisMinX + axisXRange * 0.20);
                    XAxisLabel3 = FormatTokenLabel(_axisMinX + axisXRange * 0.40);
                    XAxisLabel4 = FormatTokenLabel(_axisMinX + axisXRange * 0.60);
                    XAxisLabel5 = FormatTokenLabel(_axisMinX + axisXRange * 0.80);
                    XAxisLabel6 = FormatTokenLabel(_axisMaxX);
                    XAxisPos1 = 0;
                    XAxisPos2 = 20;
                    XAxisPos3 = 40;
                    XAxisPos4 = 60;
                    XAxisPos5 = 80;
                    XAxisPos6 = 100;

                    // Also populate the XAxisLabels collection (the code-behind positions from the
                    // collection, not the properties). The final label (axis max) is flagged
                    // IsAxisMax so it is right-aligned at the axis end.
                    var evenPositions = new[] { 0.0, 20.0, 40.0, 60.0, 80.0, 100.0 };
                    var evenTexts = new[]
                    {
                        XAxisLabel1, XAxisLabel2, XAxisLabel3,
                        XAxisLabel4, XAxisLabel5, XAxisLabel6
                    };
                    XAxisLabels.Clear();
                    for (int ei = 0; ei < evenTexts.Length; ei++)
                    {
                        XAxisLabels.Add(new XAxisLabelItem
                        {
                            Text = evenTexts[ei],
                            Position = evenPositions[ei],
                            IsAxisMax = ei == evenTexts.Length - 1
                        });
                    }
                }
            }
            else
            {
                XAxisLabel1 = XAxisLabel2 = XAxisLabel3 = XAxisLabel4 = XAxisLabel5 = XAxisLabel6 = "";
                XAxisPos1 = XAxisPos2 = XAxisPos3 = XAxisPos4 = XAxisPos5 = XAxisPos6 = 0;
            }

            // Y-axis: 0/25/50/75/100% of the axis max.
            // Both graphs use round numbers (multiples of 10) for clean Y-axis labels:
            // TTFT graph (log-scale, seconds) and Throughput graph (linear, tok/s).
            {
                // Round the axis max up to the next multiple of 10 for clean, round Y-axis labels.
                // Use a minimum of 10 to avoid the "all numbers the same" confusion when data
                // is just starting (e.g. _axisMaxY = 0.5 would otherwise round to 10, making
                // the labels 0/2.5/5/7.5/10 look nearly identical).
                var roundedMaxY = Math.Max(10.0, Math.Ceiling(_axisMaxY / 10.0) * 10.0);
                YAxisMin = "0";
                YAxis25 = (roundedMaxY * 0.25).ToString("0", CultureInfo.InvariantCulture);
                YAxis50 = (roundedMaxY * 0.50).ToString("0", CultureInfo.InvariantCulture);
                YAxis75 = (roundedMaxY * 0.75).ToString("0", CultureInfo.InvariantCulture);
                YAxisMax = roundedMaxY.ToString("0", CultureInfo.InvariantCulture);
            }
            return;
        }

        if (!_hasData)
        {
            XAxisLabel1 = "0";
            XAxisLabel2 = "25";
            XAxisLabel3 = "50";
            XAxisLabel4 = "75";
            XAxisLabel5 = "90";
            XAxisLabel6 = "100";
            XAxisPos1 = 0;
            XAxisPos2 = 20;
            XAxisPos3 = 40;
            XAxisPos4 = 60;
            XAxisPos5 = 80;
            XAxisPos6 = 100;
            YAxisMin = "0";
            YAxis25 = "0.25";
            YAxis50 = "0.5";
            YAxis75 = "0.75";
            YAxisMax = "1";
            return;
        }

        // X-axis: 6 evenly-spaced labels showing the actual X values at 0/20/40/60/80/100%
        // of the data range. The code-behind positions labels from the XAxisLabels COLLECTION
        // (PositionAxisLabelSet), not from the XAxisLabel1-6 properties, so we must populate the
        // collection here — otherwise the per-turn view (no fixed axis range) shows no X-axis
        // scale at all. The XAxisLabel1-6 props are also set for completeness.
        var xRange = _maxX - _minX;
        var xTickValues = new[]
        {
            _minX,
            _minX + xRange * 0.20,
            _minX + xRange * 0.40,
            _minX + xRange * 0.60,
            _minX + xRange * 0.80,
            _maxX,
        };
        XAxisLabel1 = _minX.ToString("0.#", CultureInfo.InvariantCulture);
        XAxisLabel2 = (_minX + xRange * 0.20).ToString("0.#", CultureInfo.InvariantCulture);
        XAxisLabel3 = (_minX + xRange * 0.40).ToString("0.#", CultureInfo.InvariantCulture);
        XAxisLabel4 = (_minX + xRange * 0.60).ToString("0.#", CultureInfo.InvariantCulture);
        XAxisLabel5 = (_minX + xRange * 0.80).ToString("0.#", CultureInfo.InvariantCulture);
        XAxisLabel6 = _maxX.ToString("0.#", CultureInfo.InvariantCulture);
        XAxisPos1 = 0;
        XAxisPos2 = 20;
        XAxisPos3 = 40;
        XAxisPos4 = 60;
        XAxisPos5 = 80;
        XAxisPos6 = 100;
        var xTickPositions = new[] { 0.0, 20.0, 40.0, 60.0, 80.0, 100.0 };
        XAxisLabels.Clear();
        for (int i = 0; i < xTickValues.Length; i++)
        {
            XAxisLabels.Add(new XAxisLabelItem
            {
                Text = xTickValues[i].ToString("0.#", CultureInfo.InvariantCulture),
                Position = xTickPositions[i],
                IsAxisMax = i == xTickValues.Length - 1
            });
        }

        // Y-axis: show the actual Y values at 0/25/50/75/100% of the effective range.
        // Use the effective Y-axis range (from GetEffectiveYAxisRange) so the Y-axis labels
        // align with the bar positions (which also use the effective range).
        GetEffectiveYAxisRange(out var yMin, out var yMax);
        var yRange = yMax - yMin;
        YAxisMin = yMin.ToString("0.#", CultureInfo.InvariantCulture);
        YAxis25 = (yMin + yRange * 0.25).ToString("0.#", CultureInfo.InvariantCulture);
        YAxis50 = (yMin + yRange * 0.50).ToString("0.#", CultureInfo.InvariantCulture);
        YAxis75 = (yMin + yRange * 0.75).ToString("0.#", CultureInfo.InvariantCulture);
        YAxisMax = yMax.ToString("0.#", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Computes the effective Y-axis range [yMin, yMax] for the current data. Used by both the
    /// Y-axis label computation and the bar positioning so they always agree. In scroll mode,
    /// the max is _maxY * 1.25 (rounded up to the next multiple of 10); in non-scroll mode, it's
    /// _axisMaxY. The min is _yAxisMinOffset (if set and less than the max), otherwise 0.
    /// </summary>
    private void GetEffectiveYAxisRange(out double yMin, out double yMax)
    {
        if (!_hasData)
        {
            yMin = 0;
            yMax = 1;
            return;
        }

        // In scroll mode (live per-turn view), _axisMaxY is not updated (it's only set in
        // SetAxisRange for the stress test). Use _maxY (the actual data max) instead. In
        // non-scroll mode, _axisMaxY may include padding (if SetAxisRange was called), but it
        // defaults to 1 if SetAxisRange was not called. Use the max of _axisMaxY and _maxY to
        // ensure the range always covers the data.
        var dataMax = _scrollMode ? _maxY : Math.Max(_axisMaxY, _maxY);

        // The Y-axis starts at 0, so the range is [0, yMax]. This keeps the y-axis stable
        // as new data points are added, avoiding jumping around as the range changes.
        yMin = 0;

        // Reserve headroom above the tallest bar for its value label. The tallest bar (value
        // = dataMax) should sit at screen position (1 - headroomFraction) * 100, leaving
        // headroomFraction * 100 of the chart height above it for the label.
        //
        // For LINEAR scale: solve (dataMax - yMin) / (yMax - yMin) = 1 - headroom
        //   yMax = yMin + (dataMax - yMin) / (1 - headroom)
        //
        // For LOG scale: solve (log(dataMax) - log(yMin)) / (log(yMax) - log(yMin)) = 1 - headroom
        //   log(yMax) = log(yMin) + (log(dataMax) - log(yMin)) / (1 - headroom)
        //   yMax = 10^(log(yMin) + (log(dataMax) - log(yMin)) / (1 - headroom))
        //
        // If no headroom is reserved, yMax = dataMax (the old behavior).
        var headroom = Math.Clamp(_labelHeadroomFraction, 0.0, 0.5);
        double rawYMax;
        if (headroom > 0)
        {
            // Linear-scale headroom: the tallest bar's position is on a linear scale, so the
            // headroom is computed in linear space to ensure the bar sits at the right screen
            // position.
            rawYMax = yMin + (dataMax - yMin) / (1.0 - headroom);
        }
        else
        {
            rawYMax = dataMax;
        }

        // Round the axis max up to the next multiple of 10 for clean, round Y-axis labels.
        // Use a minimum of 10 to avoid the "all numbers the same" confusion when data
        // is just starting.
        yMax = Math.Max(10.0, Math.Ceiling(rawYMax / 10.0) * 10.0);

        // Guard: if rounding pushed yMax back below the data max (shouldn't happen, but be
        // safe), bump it up one more step of 10.
        if (yMax <= dataMax)
        {
            yMax = Math.Ceiling((dataMax + 1) / 10.0) * 10.0;
        }
    }

    /// <summary>
    /// Sets the Y-axis labels from the data's own Y range (0/25/50/75/100% of [minY, maxY]).
    /// Used by the no-fixed-axis-range paths (the live per-turn scroll view and the generic
    /// data-range fallback). When no data exists, shows the default 0..1 scale.
    /// </summary>
    private void UpdateYAxisDataRangeLabels()
    {
        if (!_hasData)
        {
            YAxisMin = "0";
            YAxis25 = "0.25";
            YAxis50 = "0.5";
            YAxis75 = "0.75";
            YAxisMax = "1";
            return;
        }

        GetEffectiveYAxisRange(out var yMin, out var yMax);
        var yRange = yMax - yMin;

        YAxisMin = yMin.ToString("0", CultureInfo.InvariantCulture);
        YAxis25 = (yMin + yRange * 0.25).ToString("0", CultureInfo.InvariantCulture);
        YAxis50 = (yMin + yRange * 0.50).ToString("0", CultureInfo.InvariantCulture);
        YAxis75 = (yMin + yRange * 0.75).ToString("0", CultureInfo.InvariantCulture);
        YAxisMax = yMax.ToString("0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Sets the Nth X-axis label property pair (XAxisLabelN / XAxisPosN) by zero-based index.
    /// Used to keep the XAxisLabel1-6 properties in sync with the XAxisLabels collection.
    /// </summary>
    private void SetXAxisLabelProp(int index, string text, double position)
    {
        switch (index)
        {
            case 0: XAxisLabel1 = text; XAxisPos1 = position; break;
            case 1: XAxisLabel2 = text; XAxisPos2 = position; break;
            case 2: XAxisLabel3 = text; XAxisPos3 = position; break;
            case 3: XAxisLabel4 = text; XAxisPos4 = position; break;
            case 4: XAxisLabel5 = text; XAxisPos5 = position; break;
            case 5: XAxisLabel6 = text; XAxisPos6 = position; break;
        }
    }

    /// <summary>
    /// Formats a bar's Y value as a short label for display at the top of the bar.
    /// TTFT values are shown with one decimal + "s" (e.g. "42.3s"); throughput values
    /// are shown as integers (e.g. "245").
    /// </summary>
    private string FormatBarValueLabel(double value)
    {
        if (YLabel.Contains("s", StringComparison.OrdinalIgnoreCase) && !YLabel.Contains("tok", StringComparison.OrdinalIgnoreCase))
        {
            return value >= 100 ? $"{value:0} s" : $"{value:0.#} s";
        }
        return value >= 100 ? $"{value:0}" : $"{value:0.#}";
    }

    /// <summary>
    /// Builds a <see cref="StreamGeometry"/> from a list of points in the 0-100
    /// coordinate space. The geometry is a simple open polyline (MoveTo + LineTo segments).
    /// Returns <c>null</c> if the list has fewer than 2 points.
    /// </summary>
    private static Geometry? BuildLineGeometry(List<Point> points)
    {
        if (points.Count < 2)
        {
            return null;
        }

        var stream = new StreamGeometry();
        using (var context = stream.Open())
        {
            context.BeginFigure(points[0]);
            for (var i = 1; i < points.Count; i++)
            {
                context.LineTo(points[i]);
            }
        }
        return stream;
    }

    /// <summary>
    /// Builds a path string of filled rectangles (bars) from a list of points in the 0-100
    /// coordinate space. Each point becomes a bar centered on its X position, growing upward
    /// from the baseline (Y=100 in screen coords) to the point's Y value.
    ///
    /// This is the fix for the "distorted line thickness" bug: a stroked polyline rendered
    /// through a non-uniform Viewbox stretch (Stretch="Fill" on a non-square plot area) scales
    /// the stroke by different factors in X and Y, so a 2px stroke becomes ~6px wide but ~3.6px
    /// tall. A filled bar has no stroke — it's a solid rectangle that scales uniformly and
    /// looks clean at any panel size.
    ///
    /// Bar width is 14 units in the 100×100 space (≈14% of the plot width), which gives 6 bars
    /// (the 6 fill levels 25/50/75/90/100/110, spaced ~14.3% apart) almost touching one another
    /// with ~0.3-unit gaps. The X range is padded by half a bar on each side (via the axis range
    /// set in SetAxisRange) so the first and last bars are not clipped at the plot edges — all
    /// bars are the same width.
    /// Returns an empty string if the list is empty.
    ///
    /// Built as a string (not a StreamGeometry) so it works in the test environment where
    /// Avalonia's IPlatformRenderInterface is unavailable. The XAML Path control accepts a
    /// string and parses it to a Geometry on the UI thread.
    /// </summary>
    /// <summary>
    /// Formats a token count as a short label (e.g., 4096 → "4K", 16384 → "16K").
    /// </summary>
    private static string FormatTokenLabel(double tokens)
    {
        if (tokens >= 1024)
        {
            var k = tokens / 1024.0;
            return k == Math.Floor(k) ? $"{(int)k}K" : $"{k:0.#}K";
        }
        return ((int)tokens).ToString(CultureInfo.InvariantCulture);
    }

    private static string BuildBarPath(List<Point> points)
    {
        if (points.Count == 0)
        {
            return string.Empty;
        }

        // Fixed bar width: 7 units in the 100×100 space. This gives clear gaps for 4-8 bars
        // (the expected range for dynamic fill levels). Using a fixed width avoids the visual
        // "flash" that occurs when the width is recomputed as each new point is added during
        // the progressive stress test (1 point → default width, 2+ points → computed width).
        const double barWidth = 7.0;
        const double baselineY = 100.0; // bottom of the plot in screen coords

        var sb = new StringBuilder();
        foreach (var p in points)
        {
            var x0 = Math.Max(0, p.X - barWidth / 2.0);
            var x1 = Math.Min(100, p.X + barWidth / 2.0);
            var topY = Math.Max(0, Math.Min(100, p.Y)); // clamp to plot bounds
            var barHeight = baselineY - topY;
            if (barHeight < 0.5)
            {
                // Tiny bar (near-zero value) — give it a minimum visible height.
                barHeight = 0.5;
                topY = baselineY - barHeight;
            }
            sb.Append($"M{x0.ToString("0.#", CultureInfo.InvariantCulture)},{topY.ToString("0.#", CultureInfo.InvariantCulture)} ");
            sb.Append($"L{x1.ToString("0.#", CultureInfo.InvariantCulture)},{topY.ToString("0.#", CultureInfo.InvariantCulture)} ");
            sb.Append($"L{x1.ToString("0.#", CultureInfo.InvariantCulture)},{baselineY.ToString("0.#", CultureInfo.InvariantCulture)} ");
            sb.Append($"L{x0.ToString("0.#", CultureInfo.InvariantCulture)},{baselineY.ToString("0.#", CultureInfo.InvariantCulture)} Z ");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Builds a path of short horizontal dashed lines (tick marks) at each ghost point's position.
    /// Each line is ~6% of the plot width, centered on the point's X coordinate, at the point's
    /// Y height. This replaces the full rectangle outlines with a much thinner reference mark
    /// that shows where the previous run's bars topped out.
    /// </summary>
    private static string BuildGhostLines(List<Point> points)
    {
        if (points.Count == 0)
        {
            return string.Empty;
        }

        const double lineHalfWidth = 3.0; // 3% each side = 6% total width

        var sb = new StringBuilder();
        foreach (var p in points)
        {
            var x0 = Math.Max(0, p.X - lineHalfWidth);
            var x1 = Math.Min(100, p.X + lineHalfWidth);
            var y = Math.Max(0, Math.Min(100, p.Y)); // clamp to plot bounds
            sb.Append($"M{x0.ToString("0.#", CultureInfo.InvariantCulture)},{y.ToString("0.#", CultureInfo.InvariantCulture)} ");
            sb.Append($"L{x1.ToString("0.#", CultureInfo.InvariantCulture)},{y.ToString("0.#", CultureInfo.InvariantCulture)} ");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Builds a path of short horizontal dashed lines at the TOP of the chart (Y=0 in screen
    /// coords) for each failure point. This replaces the old full-height red bar, which
    /// implied the failure had a TTFT value. The dashed line clearly marks "the test stopped
    /// here" without implying a performance value. Same shape as <see cref="BuildGhostLines"/>
    /// but always at Y=0 (the top of the chart).
    /// </summary>
    private static string BuildFailureLines(List<Point> points)
    {
        if (points.Count == 0)
        {
            return string.Empty;
        }

        const double lineHalfWidth = 3.0; // 3% each side = 6% total width
        const double topY = 0.0; // top of the chart (max TTFT)

        var sb = new StringBuilder();
        foreach (var p in points)
        {
            var x0 = Math.Max(0, p.X - lineHalfWidth);
            var x1 = Math.Min(100, p.X + lineHalfWidth);
            sb.Append($"M{x0.ToString("0.#", CultureInfo.InvariantCulture)},{topY.ToString("0.#", CultureInfo.InvariantCulture)} ");
            sb.Append($"L{x1.ToString("0.#", CultureInfo.InvariantCulture)},{topY.ToString("0.#", CultureInfo.InvariantCulture)} ");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Parses a path string into a <see cref="Geometry"/>. Returns <c>null</c> when the string
    /// is empty or when the Avalonia platform is unavailable (test environment). The XAML
    /// <c>Path</c> control also accepts a string directly, so a <c>null</c> here is only a
    /// problem for the test assertions, not the live UI.
    /// </summary>
    private static Geometry? TryParseGeometry(string pathData)
    {
        if (string.IsNullOrEmpty(pathData))
        {
            return null;
        }

        try
        {
            return Geometry.Parse(pathData);
        }
        catch
        {
            // Avalonia platform not initialized (test environment) — return null.
            // The XAML Path control will parse the string itself on the UI thread.
            return null;
        }
    }
}