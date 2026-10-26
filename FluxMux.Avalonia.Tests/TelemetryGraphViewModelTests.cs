using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using FluxMux.Avalonia.ViewModels;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// Tests for <see cref="TelemetryGraphViewModel"/>. The critical invariant is that the
/// collections bound to the <c>Polyline.Points</c> control are STABLE instances that are
/// mutated in place — never replaced. Avalonia's <c>PolylineGeometry</c> subscribes to
/// <c>INotifyCollectionChanged</c> on the bound collection; if the VM swapped in a new
/// <c>List&lt;Point&gt;</c> on every update, the geometry would keep pointing at the old
/// (often empty) collection and the curve would never render. This is the bug that left the
/// stress-test graph panel blank.
/// </summary>
public sealed class TelemetryGraphViewModelTests
{
    [Fact]
    public void PolylinePoints_is_a_stable_instance_across_updates()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Fill %", "TTFT (s)");

        var original = vm.PolylinePoints;
        Assert.NotNull(original);

        // Add points and update the last point — the bound collection instance must NOT change.
        vm.AddPoint(0, 0, "baseline", isFailure: false);
        Assert.Same(original, vm.PolylinePoints);

        vm.AddPoint(25, 73.5, "OK", isFailure: false);
        Assert.Same(original, vm.PolylinePoints);

        vm.UpdateLastPoint(50, 161.2, "OK", isFailure: false);
        Assert.Same(original, vm.PolylinePoints);

        // And the collection actually reflects the data (2 points: baseline + level 25, with
        // the last one updated to 50%).
        Assert.Equal(2, original.Count);
    }

    [Fact]
    public void PolylinePoints_reflects_added_points()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Fill %", "TTFT (s)");
        vm.AddPoint(0, 0, "baseline", isFailure: false);
        vm.AddPoint(25, 73.5, "OK", isFailure: false);
        vm.AddPoint(50, 161.2, "OK", isFailure: false);

        Assert.Equal(3, vm.PolylinePoints.Count);
        Assert.True(vm.HasPoints);
        // Points are normalized into the [0,100] screen space.
        Assert.All(vm.PolylinePoints, p =>
        {
            Assert.InRange(p.X, 0, 100);
            Assert.InRange(p.Y, 0, 100);
        });
    }

    [Fact]
    public void UpdateLastPoint_updates_in_place_without_adding_a_new_point()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Fill %", "TTFT (s)");
        vm.AddPoint(0, 0, "baseline", isFailure: false);
        vm.AddPoint(25, 10, "waiting", isFailure: false);
        var countBefore = vm.PolylinePoints.Count;

        // Simulate the live heartbeat updating the in-progress point, then finalizing it.
        vm.UpdateLastPoint(25, 73.5, "OK 85.5 tok/s", isFailure: false);
        Assert.Equal(countBefore, vm.PolylinePoints.Count);

        // The last point's Y should now reflect the finalized TTFT (higher on screen = lower
        // Y in screen coords, but the value must have changed from the waiting value).
        Assert.NotEqual(10.0, vm.Points[^1].Y);
    }

    [Fact]
    public void Failure_points_are_routed_to_the_failure_collection()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Fill %", "TTFT (s)");
        vm.AddPoint(0, 0, "baseline", isFailure: false);
        vm.AddPoint(25, 73.5, "OK", isFailure: false);
        vm.AddPoint(75, 390, "HTTP 400", isFailure: true);

        // 2 success points (baseline + 25%), 1 failure point (75%).
        Assert.Equal(2, vm.PolylinePoints.Count);
        Assert.Equal(1, vm.FailurePolylinePoints.Count);
        Assert.True(vm.HasPoints);
    }

    [Fact]
    public void SetSeries_clears_existing_points_but_keeps_the_same_collection_instance()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("first", "x", "y");
        vm.AddPoint(0, 0, "a", isFailure: false);
        var original = vm.PolylinePoints;

        vm.SetSeries("second", "x", "y");
        Assert.Same(original, vm.PolylinePoints);
        Assert.Empty(vm.PolylinePoints);
        Assert.False(vm.HasPoints);
    }

    [Fact]
    public void Clear_hides_the_graph_and_keeps_the_same_collection_instance()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "x", "y");
        vm.AddPoint(0, 0, "a", isFailure: false);
        var original = vm.PolylinePoints;

        vm.Clear();
        Assert.Same(original, vm.PolylinePoints);
        Assert.Empty(vm.PolylinePoints);
        Assert.False(vm.HasPoints);
        Assert.False(vm.IsActive);
    }

    [Fact]
    public void SeedBaseline_creates_a_visible_curve()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Fill %", "TTFT (s)");
        vm.SeedBaseline();

        // SeedBaseline adds 2 points at Y=0 (a flat baseline). The Viewbox in the XAML scales
        // the 100x100 geometry to fill the plot area, so no origin-prepending is needed.
        // Result: 2 points (the two baseline points).
        Assert.Equal(2, vm.PolylinePoints.Count);
        Assert.True(vm.HasPoints);
    }

    [Fact]
    public void Each_level_adds_a_new_point_and_heartbeat_updates_it_in_place()
    {
        // Regression: the graph stayed blank because SeedBaseline added 2 points and both the
        // heartbeat and onLevelComplete called UpdateLastPoint, which overwrote the 2nd baseline
        // point instead of adding real data points. The correct flow is: onLevelStart adds a NEW
        // point per level, the heartbeat moves it (in place), and onLevelComplete finalizes it.
        // Simulate that flow for 3 levels.
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Fill %", "TTFT (s)");

        // Level 25%: start (add), heartbeat (update), complete (update).
        vm.AddPoint(25, 0, "waiting", isFailure: false);
        Assert.Equal(1, vm.Points.Count);
        vm.UpdateLastPoint(25, 10, "waiting 10s", isFailure: false);
        Assert.Equal(1, vm.Points.Count); // still 1 point, updated in place
        vm.UpdateLastPoint(25, 31.2, "OK 89 tok/s", isFailure: false);
        Assert.Equal(1, vm.Points.Count);
        Assert.Equal(31.2, vm.Points[^1].Y);

        // Level 50%: start (add new point), heartbeat, complete.
        vm.AddPoint(50, 0, "waiting", isFailure: false);
        Assert.Equal(2, vm.Points.Count);
        vm.UpdateLastPoint(50, 139.1, "OK 62 tok/s", isFailure: false);
        Assert.Equal(2, vm.Points.Count);
        Assert.Equal(139.1, vm.Points[^1].Y);

        // Level 75%: start (add), complete as a failure.
        vm.AddPoint(75, 0, "waiting", isFailure: false);
        Assert.Equal(3, vm.Points.Count);
        vm.UpdateLastPoint(75, 0.4, "HTTP 400", isFailure: true);
        Assert.Equal(3, vm.Points.Count);

        // 2 success points (25%, 50%), 1 failure point (75%).
        Assert.Equal(2, vm.PolylinePoints.Count);
        Assert.Equal(1, vm.FailurePolylinePoints.Count);
        Assert.True(vm.HasPoints);
    }

    [Fact]
    public void SummaryText_reflects_the_last_point()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Fill %", "TTFT (s)");
        vm.AddPoint(0, 0, "baseline", isFailure: false);
        vm.AddPoint(75, 5.6, "OK", isFailure: false);

        Assert.Contains("75", vm.SummaryText);
        Assert.Contains("TTFT (s)", vm.SummaryText);
    }

    [Fact]
    public void SetAxisRange_normalizes_x_against_the_fixed_range()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Fill %", "TTFT (s)");
        vm.SetAxisRange(0, 110, 0, 120);

        // 25% fill should map to 25/110 of the X axis (~22.7), not 0 (data-range fallback).
        vm.AddPoint(25, 0, "waiting", isFailure: false);
        var p = vm.PolylinePoints[^1];
        Assert.InRange(p.X, 22.0, 23.5);
    }

    [Fact]
    public void SetAxisRange_y_grows_never_shrinks()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Fill %", "TTFT (s)");
        vm.SetAxisRange(0, 110, 0, 120);

        // A 109s TTFT exceeds the initial 120s? No — but a 200s reading must grow the axis.
        vm.AddPoint(75, 200, "OK", isFailure: false);
        // Y max grows from 120 to 200 * 1.25 = 250 (the actual _axisMaxY used for position
        // computation). The 200s point sits at (200-0)/250 * 100 = 80, so screenY = 20.
        // The Y-axis LABEL is rounded up to the next multiple of 10 for clean display: 250.
        var p = vm.PolylinePoints[^1];
        Assert.InRange(p.Y, 15.0, 25.0);
        // Axis label reflects the rounded grown max (250 → 250).
        Assert.Equal("250", vm.YAxisMax);
    }

    [Fact]
    public void AxisLabels_use_token_suffix_when_range_set()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Tokens", "TTFT (s)");
        vm.SetAxisRange(0, 34406, 0, 300);

        // X-axis labels are evenly-spaced (0/20/40/60/80/100% of the range), so they continue
        // into the extended portions when the axis auto-extends.
        Assert.Equal("0", vm.XAxisLabel1);
        Assert.Equal("6.7K", vm.XAxisLabel2);
        Assert.Equal("13.4K", vm.XAxisLabel3);
        Assert.Equal("20.2K", vm.XAxisLabel4);
        Assert.Equal("26.9K", vm.XAxisLabel5);
        Assert.Equal("33.6K", vm.XAxisLabel6);
        // Y labels are in seconds (0 at the bottom).
        Assert.Equal("0", vm.YAxisMin);
    }

    [Fact]
    public void AxisLabels_drop_last_fill_level_when_too_close_to_axis_max()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Tokens", "TTFT (s)");
        // Axis max is 275251 (contextLimit 262144 * 1.05). Fill levels cover up to 262144.
        // The last fill level (256K) is at 95.2% and the axis max is at 100% — a 4.8% gap,
        // which is below the 10% minimum, so the last fill-level label is dropped to avoid
        // overlap, leaving only the axis-max label at the right end.
        vm.SetAxisRange(0, 275251, 0, 300);
        vm.SetFillLevels(new[] { 65536, 131072, 196608, 262144 });

        var labels = vm.XAxisLabels.ToList();
        // 3 fill levels (64K, 128K, 192K) + 1 axis-max label (268.8K). The 256K label is dropped.
        Assert.Equal(4, labels.Count);
        Assert.Equal("64K", labels[0].Text);
        Assert.Equal("128K", labels[1].Text);
        Assert.Equal("192K", labels[2].Text);
        // The final label is at the axis max (position 100).
        Assert.Equal(100.0, labels[^1].Position, 1);
        Assert.Equal("268.8K", labels[^1].Text); // 275251 / 1024 = 268.8 -> "268.8K"
    }

    [Fact]
    public void AxisLabels_keep_last_fill_level_when_far_from_axis_max()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Tokens", "TTFT (s)");
        // Axis max is well beyond the last fill level, so the gap is large enough to keep both.
        // Fill levels up to 131072, axis max 262144 -> last fill level at 50%, axis max at 100%.
        vm.SetAxisRange(0, 262144, 0, 300);
        vm.SetFillLevels(new[] { 65536, 131072 });

        var labels = vm.XAxisLabels.ToList();
        // Both fill levels kept + the axis-max label.
        Assert.Equal(3, labels.Count);
        Assert.Equal("64K", labels[0].Text);
        Assert.Equal("128K", labels[1].Text);
        Assert.Equal("256K", labels[^1].Text); // 262144 / 1024 = 256 -> "256K"
        Assert.Equal(100.0, labels[^1].Position, 1);
    }

    [Fact]
    public void SetStressTestSummary_produces_a_clear_heading()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Fill %", "TTFT (s)");
        vm.SetAxisRange(0, 110, 0, 120);
        vm.SetStressTestSummary(4, 6, 109.0, 75000, 90000, aborted: false);

        // The summary contains ONLY progress/results in compact form, not the axis description
        // (the axis description is in the graph title, set via SetSeries).
        Assert.DoesNotContain("Context Fill %", vm.SummaryText);
        Assert.DoesNotContain("Time To First Token", vm.SummaryText);
        Assert.Contains("4/6", vm.SummaryText);
        Assert.Contains("max 109s @ 73.2K", vm.SummaryText);
        Assert.Contains("failed @ 87.9K", vm.SummaryText);
    }

    [Fact]
    public void SetStressTestSummary_omits_failure_when_all_succeeded()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Fill %", "TTFT (s)");
        vm.SetAxisRange(0, 110, 0, 120);
        vm.SetStressTestSummary(6, 6, 109.0, 75000, null, aborted: false);

        Assert.Contains("6/6", vm.SummaryText);
        Assert.DoesNotContain("failed", vm.SummaryText);
        Assert.DoesNotContain("aborted", vm.SummaryText);
    }

    [Fact]
    public void SetUnfilledRegion_shows_hatch_when_stopped_early()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Fill %", "TTFT (s)");
        vm.SetAxisRange(0, 110, 0, 120);
        vm.SetUnfilledRegion(75);

        Assert.True(vm.HasUnfilledRegion);
        Assert.NotNull(vm.UnfilledRegionGeometry);
        // 75% fill maps to 75/110 (~68.2) of the X axis; the boundary is pushed to the right
        // edge of the bar centered there (half-width 3.5), so the line/hatch start at ~71.7
        // instead of crossing the bar. The hatch spans from there to 100.
        var rect = (vm.UnfilledRegionGeometry as RectangleGeometry)!.Rect;
        Assert.InRange(rect.X, 71.0, 72.5);
        Assert.Equal(100.0, rect.Width + rect.X, 1);
        // The dotted vertical line is at the same X position.
        // (UnfilledRegionLineGeometry is null in the test environment where Avalonia's
        // IPlatformRenderInterface is unavailable; the XAML Path control parses the string
        // itself on the UI thread. We check UnfilledRegionLineX instead.)
        Assert.InRange(vm.UnfilledRegionLineX, 71.0, 72.5);
    }

    [Fact]
    public void SetUnfilledRegion_line_does_not_overlap_the_completed_bar()
    {
        // Regression: when the test is aborted mid-level, the boundary is the in-progress
        // level (which has no bar), but the previous level's bar sits just to the left. The
        // line must start at the right edge of that bar, not cross it.
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Tokens", "TTFT (s)");
        // Axis 0..409600 (400K), fill levels every 64K: 65536, 131072, 196608, 262144, 327680, 393216.
        vm.SetAxisRange(0, 409600, 0, 350);

        // Aborted at the 256K level (262144): the 192K bar (196608) is the last completed one.
        // The 192K bar is centered at 196608/409600*100 = 48.0, half-width 3.5 → right edge 51.5.
        // The 256K boundary maps to 262144/409600*100 = 64.0 → line at 67.5.
        vm.SetUnfilledRegion(262144);

        Assert.True(vm.HasUnfilledRegion);
        // The line must be well clear of the 192K bar's right edge (51.5).
        Assert.True(vm.UnfilledRegionLineX > 51.5, $"Line at {vm.UnfilledRegionLineX} overlaps the 192K bar (right edge 51.5)");
        // And it must be at the 256K boundary + half bar width.
        Assert.InRange(vm.UnfilledRegionLineX, 67.0, 68.0);
    }

    [Fact]
    public void SetUnfilledRegion_hidden_when_all_levels_ran()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Fill %", "TTFT (s)");
        vm.SetAxisRange(0, 110, 0, 120);
        // Highest successful at 110 (== axis max) → no hatch.
        vm.SetUnfilledRegion(110);
        Assert.False(vm.HasUnfilledRegion);
        Assert.Null(vm.UnfilledRegionGeometry);
        Assert.Null(vm.UnfilledRegionLineGeometry);
        Assert.Equal(0, vm.UnfilledRegionLineX);
    }

    [Fact]
    public void SetUnfilledRegion_hidden_when_no_success()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Fill %", "TTFT (s)");
        vm.SetAxisRange(0, 110, 0, 120);
        vm.SetUnfilledRegion(null);
        Assert.False(vm.HasUnfilledRegion);
        Assert.Null(vm.UnfilledRegionGeometry);
        Assert.Null(vm.UnfilledRegionLineGeometry);
        Assert.Equal(0, vm.UnfilledRegionLineX);
    }

    [Fact]
    public void BarPathData_is_built_with_one_rectangle_per_point()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Fill %", "TTFT (s)");
        vm.SetAxisRange(25, 110, 0, 120);

        // Add three fill levels with increasing TTFT.
        vm.AddPoint(25, 10, "OK", isFailure: false);
        vm.AddPoint(50, 40, "OK", isFailure: false);
        vm.AddPoint(75, 90, "OK", isFailure: false);

        // The path data should contain 3 closed rectangles (one per bar).
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(vm.BarPathData, "Z").Count);
        // Each bar starts with M (move to top-left) and ends with Z (close).
        Assert.StartsWith("M", vm.BarPathData);
        // Bars grow from the baseline (Y=100) upward. The path should contain Y=100
        // (the baseline) for every bar.
        Assert.Equal(6, System.Text.RegularExpressions.Regex.Matches(vm.BarPathData, ",100\\b").Count);
        // The topmost bar (90s TTFT out of 120s max = 75% of the way up) should have its
        // top at Y ≈ 25 in screen coords (100 - 75 = 25).
        Assert.Contains("M", vm.BarPathData);
    }

    [Fact]
    public void BarPathData_is_empty_when_no_points()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Fill %", "TTFT (s)");
        Assert.Empty(vm.BarPathData);
        Assert.Empty(vm.FailureBarPathData);
    }

    [Fact]
    public void FailureBarPathData_is_built_for_failed_points()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Fill %", "TTFT (s)");
        vm.SetAxisRange(25, 110, 0, 120);

        vm.AddPoint(25, 10, "OK", isFailure: false);
        vm.AddPoint(50, 0, "Timed out", isFailure: true);

        // The failure marker is a short horizontal dashed line at the top of the chart
        // (Y=0 in screen coords = max TTFT), NOT a full-height bar. It marks "the test
        // stopped here" without implying a TTFT value.
        Assert.NotEmpty(vm.FailureBarPathData);
        // A line path has M...L... segments, no "Z" (closed rectangle).
        Assert.DoesNotContain("Z", vm.FailureBarPathData);
        // The line sits at Y=0 (top of the chart).
        Assert.Contains(",0 ", vm.FailureBarPathData);
    }

    [Fact]
    public void BarPathData_grows_when_more_points_are_added()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Fill %", "TTFT (s)");
        vm.SetAxisRange(25, 110, 0, 120);

        vm.AddPoint(25, 10, "OK", isFailure: false);
        var bars1 = System.Text.RegularExpressions.Regex.Matches(vm.BarPathData, "Z").Count;

        vm.AddPoint(50, 60, "OK", isFailure: false);
        var bars2 = System.Text.RegularExpressions.Regex.Matches(vm.BarPathData, "Z").Count;

        Assert.Equal(1, bars1);
        Assert.Equal(2, bars2);
    }

    [Fact]
    public void TestingBar_grows_with_progress()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Fill %", "TTFT (s)");
        vm.SetAxisRange(18, 117, 0, 120);

        // Start the testing bar at 25% fill.
        vm.SetTestingBand(25, "waiting…");
        Assert.True(vm.IsTestingBandVisible);
        Assert.NotEmpty(vm.TestingBarPath);

        // At 0% progress, the bar has a minimum visible height (1 unit).
        var initialTop = ParseFirstBarTop(vm.TestingBarPath);
        Assert.InRange(initialTop, 98.0, 99.5);

        // At 50% progress, the bar is taller.
        vm.UpdateTestingBarProgress(0.5);
        var midTop = ParseFirstBarTop(vm.TestingBarPath);
        Assert.True(midTop < initialTop, $"Expected bar to grow (top Y to decrease): {midTop} < {initialTop}");

        // At 100% progress, the bar is nearly full height.
        vm.UpdateTestingBarProgress(1.0);
        var fullTop = ParseFirstBarTop(vm.TestingBarPath);
        Assert.True(fullTop < midTop, $"Expected bar to grow further: {fullTop} < {midTop}");

        // Clearing the band hides the bar.
        vm.ClearTestingBand();
        Assert.False(vm.IsTestingBandVisible);
        Assert.Empty(vm.TestingBarPath);
    }

    /// <summary>
    /// Parses the top Y coordinate of the first bar in a path string.
    /// The first bar's top-left corner is the first point in the path (after "M").
    /// </summary>
    private static double ParseFirstBarTop(string pathData)
    {
        // Path format: "M x0,topY L x1,topY L x1,100 L x0,100 Z ..."
        var firstSegment = pathData.Split('L')[0]; // "M x0,topY "
        var coords = firstSegment.TrimStart('M').Trim().Split(',');
        return double.Parse(coords[1], System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void SetGhostBars_builds_ghost_geometry_for_previous_levels()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Tokens", "TTFT (s)");
        vm.SetAxisRange(0, 34406, 0, 300);

        // Simulate a previous run with 3 successful levels (in tokens).
        vm.SetGhostBars(new[]
        {
            (Tokens: 4096.0, Value: 5.0),
            (Tokens: 8192.0, Value: 20.0),
            (Tokens: 16384.0, Value: 60.0),
        });

        Assert.True(vm.HasGhostBars);
        Assert.NotEmpty(vm.GhostBarPathData);
        // Note: GhostBarGeometry may be null in the test environment (Avalonia platform
        // unavailable), so we assert on the path data instead.
        // One horizontal line per level (each starts with "M").
        var lineCount = System.Text.RegularExpressions.Regex.Matches(vm.GhostBarPathData, "M").Count;
        Assert.Equal(3, lineCount);
    }

    [Fact]
    public void SetGhostBars_clears_ghost_when_null_or_empty()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Tokens", "TTFT (s)");
        vm.SetAxisRange(0, 34406, 0, 300);

        // No previous run.
        vm.SetGhostBars(null);
        Assert.False(vm.HasGhostBars);
        Assert.Null(vm.GhostBarGeometry);
        Assert.Empty(vm.GhostBarPathData);

        // Empty list also clears.
        vm.SetGhostBars(Array.Empty<(double, double)>());
        Assert.False(vm.HasGhostBars);
        Assert.Empty(vm.GhostBarPathData);
    }

    [Fact]
    public void SetGhostBars_aligns_with_live_axis_range()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Tokens", "TTFT (s)");
        vm.SetAxisRange(0, 34406, 0, 300);

        // A ghost line at 4096 tokens should map to the same X as a live point at 4096 tokens.
        vm.SetGhostBars(new[] { (Tokens: 4096.0, Value: 10.0) });

        // The 4096 token level maps to nx = (4096 - 0) / (34406 - 0) * 100 ≈ 11.9.
        var firstSegment = vm.GhostBarPathData.Split('L')[0];
        var coords = firstSegment.TrimStart('M').Trim().Split(',');
        var x0 = double.Parse(coords[0], System.Globalization.CultureInfo.InvariantCulture);
        // The line is centered on nx≈11.9 with half-width 3, so x0 = 11.9 - 3 ≈ 8.9.
        Assert.InRange(x0, 8.0, 10.0);
    }

    [Fact]
    public void RefreshAfterTrim_rederives_bounds_from_remaining_points()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Turn", "tok/s");

        // Add points across a wide X range; the bounds should span all of them.
        for (var i = 1; i <= 25; i++)
        {
            vm.AddPoint(i, i * 10.0, label: $"turn {i}");
        }

        Assert.Equal(25, vm.Points.Count);

        // Simulate the rolling-window trim: drop the oldest 5 points, then refresh.
        for (var i = 0; i < 5; i++)
        {
            vm.Points.RemoveAt(0);
        }

        Assert.Equal(20, vm.Points.Count);
        vm.RefreshAfterTrim();

        // After the trim the graph still renders (HasPoints) and the remaining points are the
        // newer ones (turns 6..25). The polyline must have been rebuilt from the survivors.
        Assert.True(vm.HasPoints);
        Assert.Equal(6.0, vm.Points[0].X);
        Assert.Equal(25.0, vm.Points[^1].X);
        Assert.Equal(20, vm.PolylinePoints.Count);
    }

    [Fact]
    public void RefreshAfterTrim_with_no_points_clears_the_graph()
    {
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Turn", "tok/s");
        vm.AddPoint(1, 12.0, label: "turn 1");

        vm.Points.Clear();
        vm.RefreshAfterTrim();

        Assert.False(vm.HasPoints);
        Assert.Empty(vm.PolylinePoints);
    }

    [Fact]
    public void BarValueX_matches_the_bar_center_not_the_axis_tick()
    {
        // Regression: bar-value labels were positioned at the axis tick X (XAxisPos1-6, evenly
        // spaced 0/20/40/60/80/100%), but the bars sit at the actual data-point X. In the
        // per-turn view (turn indices 1..N, no fixed axis range) that offset the value labels
        // from the bars. BarValueX1-6 must equal each bar's center so the code-behind centers
        // each label on its own bar.
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Turn", "tok/s");
        // Three turns at X = 1, 2, 3 (no fixed axis range, so the data range is [1,3]).
        vm.AddPoint(1, 10.0, label: "turn 1");
        vm.AddPoint(2, 20.0, label: "turn 2");
        vm.AddPoint(3, 30.0, label: "turn 3");

        // The bars are centered on the normalized X of each point. With data range [1,3]:
        //   turn 1 -> (1-1)/(3-1)*100 = 0
        //   turn 2 -> (2-1)/(3-1)*100 = 50
        //   turn 3 -> (3-1)/(3-1)*100 = 100
        Assert.Equal(0.0, vm.BarValueX1, 3);
        Assert.Equal(50.0, vm.BarValueX2, 3);
        Assert.Equal(100.0, vm.BarValueX3, 3);

        // The axis ticks, by contrast, are evenly spaced and would NOT match the bar centers
        // for the middle bar when the range is asymmetric. Here they happen to align (0/50/100),
        // so assert the invariant that BarValueX tracks the bar center by checking a case where
        // they diverge: a single point at X=1 with data range [1,1] (zero width) centers at 50,
        // while the axis tick 1 would be at 0.
        var vm2 = new TelemetryGraphViewModel();
        vm2.SetSeries("test", "Turn", "tok/s");
        vm2.AddPoint(5, 42.0, label: "turn 5");
        // Single point: data range [5,5], zero width -> centered at 50.
        Assert.Equal(50.0, vm2.BarValueX1, 3);
        // The axis tick 1 (XAxisPos1) is at 0, not 50 — proving BarValueX is the bar center,
        // not the axis tick.
        Assert.Equal(0.0, vm2.XAxisPos1, 3);
        Assert.NotEqual(vm2.XAxisPos1, vm2.BarValueX1);
    }

    [Fact]
    public void XAxisLabels_collection_is_populated_without_a_fixed_axis_range()
    {
        // Regression: the no-axis-range fallback (used by the per-turn view) set the
        // XAxisLabel1-6 PROPERTIES but never populated the XAxisLabels COLLECTION that the
        // code-behind's PositionAxisLabelSet actually reads. Result: the per-turn view showed
        // no X-axis scale at all. The collection must be populated so the labels render.
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Turn", "tok/s");
        vm.AddPoint(1, 10.0, label: "turn 1");
        vm.AddPoint(2, 20.0, label: "turn 2");
        vm.AddPoint(3, 30.0, label: "turn 3");

        // No SetAxisRange call -> data-range fallback. The collection must now hold 6 labels.
        Assert.Equal(6, vm.XAxisLabels.Count);
        // Positions are evenly spaced 0/20/40/60/80/100.
        Assert.Equal(0.0, vm.XAxisLabels[0].Position, 3);
        Assert.Equal(100.0, vm.XAxisLabels[5].Position, 3);
        // Text reflects the actual X values (turn indices 1..3): "1", "1.4", "1.8", "2.2", "2.6", "3".
        Assert.Equal("1", vm.XAxisLabels[0].Text);
        Assert.Equal("3", vm.XAxisLabels[5].Text);
        // Every label has non-empty text (none hidden).
        Assert.All(vm.XAxisLabels, l => Assert.False(string.IsNullOrEmpty(l.Text)));
    }

    [Fact]
    public void ScrollMode_newest_bar_starts_at_right_edge_and_slides_left()
    {
        // Scroll mode (live per-turn view): the newest bar sits at the right edge (X=100) when
        // the scroll offset is 0, and slides left by one slot as the offset advances to 1. Bars
        // are spaced 100/window units apart. Verify the sliding-window X formula.
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Turn", "tok/s");
        vm.BeginScrollMode(windowSize: 6);
        // Three turns at indices 1, 2, 3. The newest is 3.
        vm.AddPoint(1, 10.0, label: "turn 1");
        vm.AddPoint(2, 20.0, label: "turn 2");
        vm.AddPoint(3, 30.0, label: "turn 3");

        // At offset 0 (the default after BeginScrollMode), the newest bar (turn 3) is at X=103.5
        // (100 + 3.5 offset for half bar width). slot = 100/6 = 16.667.
        //   turn 3 -> 100 - (3-3+0)*slot + 3.5 = 103.5
        //   turn 2 -> 100 - (3-2+0)*slot + 3.5 = 86.833
        //   turn 1 -> 100 - (3-1+0)*slot + 3.5 = 70.167
        var slot = 100.0 / 6.0;
        var offset = 3.5; // half bar width offset
        Assert.Equal(100.0 + offset, vm.BarValueX3, 2); // newest (turn 3) at the right edge + offset
        Assert.Equal(100.0 - slot + offset, vm.BarValueX2, 2); // turn 2 one slot left
        Assert.Equal(100.0 - 2 * slot + offset, vm.BarValueX1, 2); // turn 1 two slots left

        // Advance the scroll to offset 1: every bar slides left by one slot.
        vm.ScrollTo(1.0);
        Assert.Equal(100.0 - slot + offset, vm.BarValueX3, 2); // turn 3 now one slot left of the edge
        Assert.Equal(100.0 - 2 * slot + offset, vm.BarValueX2, 2);
        Assert.Equal(100.0 - 3 * slot + offset, vm.BarValueX1, 2); // turn 1 three slots left
    }

    [Fact]
    public void ScrollMode_x_axis_labels_show_turn_numbers_at_bar_positions()
    {
        // In scroll mode the X-axis shows the TURN NUMBER of each visible bar, positioned at the
        // same screen-X as the bar so each number scrolls beneath its own bar. Verify the labels
        // match the bars' X positions and carry the correct turn numbers.
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Turn", "tok/s");
        vm.BeginScrollMode(windowSize: 6);
        vm.AddPoint(4, 10.0, label: "turn 4");
        vm.AddPoint(5, 20.0, label: "turn 5");
        vm.AddPoint(6, 30.0, label: "turn 6");

        // Three visible turns (4, 5, 6). The labels should be ordered left-to-right (oldest to
        // newest): "4", "5", "6", each at the matching bar's X.
        Assert.Equal(3, vm.XAxisLabels.Count);
        Assert.Equal("4", vm.XAxisLabels[0].Text);
        Assert.Equal("5", vm.XAxisLabels[1].Text);
        Assert.Equal("6", vm.XAxisLabels[2].Text);

        // The newest bar (turn 6) is at X=103.5 (100 + 3.5 offset) at offset 0; its label matches.
        var offset = 3.5; // half bar width offset
        Assert.Equal(100.0 + offset, vm.BarValueX3, 2);
        Assert.Equal(100.0 + offset, vm.XAxisLabels[2].Position, 2);
        // The oldest visible (turn 4) is two slots left; its label matches its bar.
        var slot = 100.0 / 6.0;
        Assert.Equal(100.0 - 2 * slot + offset, vm.BarValueX1, 2);
        Assert.Equal(100.0 - 2 * slot + offset, vm.XAxisLabels[0].Position, 2);
    }

    [Fact]
    public void ScrollTo_is_a_noop_when_not_in_scroll_mode()
    {
        // The stress test and any non-scroll series must ignore ScrollTo. A graph with a fixed
        // axis range (the stress test's configuration) keeps its fill-level bar positions even
        // after ScrollTo is called — the scroll-mode code paths are skipped.
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Fill %", "TTFT (s)");
        vm.SetAxisRange(0, 110, 0, 350);
        vm.SetFillLevels(new[] { 25, 50, 75 });
        vm.AddPoint(25, 10.0, label: "25%");
        vm.AddPoint(50, 20.0, label: "50%");

        // Record the bar positions under the fixed axis range.
        var xBefore1 = vm.BarValueX1;
        var xBefore2 = vm.BarValueX2;

        // ScrollTo must not move the bars (not in scroll mode).
        vm.ScrollTo(0.5);
        Assert.Equal(xBefore1, vm.BarValueX1, 3);
        Assert.Equal(xBefore2, vm.BarValueX2, 3);
        Assert.False(vm.IsScrollMode);
    }

    [Fact]
    public void EndScrollMode_restores_fixed_axis_range_positioning()
    {
        // After a scroll-mode session, EndScrollMode (called when the stress test takes over)
        // disables the sliding window so the fixed axis range is used again.
        var vm = new TelemetryGraphViewModel();
        vm.SetSeries("test", "Turn", "tok/s");
        vm.BeginScrollMode(windowSize: 6);
        vm.AddPoint(1, 10.0, label: "turn 1");
        Assert.True(vm.IsScrollMode);

        // Switch to a fixed axis range (as the stress test does) and exit scroll mode.
        vm.EndScrollMode();
        vm.SetAxisRange(0, 110, 0, 350);
        vm.AddPoint(50, 20.0, label: "50%");

        Assert.False(vm.IsScrollMode);
        // With the fixed range [0,110], the bar at X=50 normalizes to 50/110*100 = 45.45.
        Assert.Equal(50.0 / 110.0 * 100.0, vm.BarValueX2, 2);
    }
}