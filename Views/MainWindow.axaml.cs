using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using FluxMux.Avalonia.Services;
using FluxMux.Avalonia.ViewModels;

namespace FluxMux.Avalonia.Views;

public partial class MainWindow : Window
{
    private ScaleTransform? _uiScaleTransform;
    private string? _draggedPriorityName;
    private string? _draggedPriorityCollection;
    private ListBox? _draggedPriorityList;
    private RouteSlotViewModel? _draggedQuickSelectSlot;
    private bool _quickSelectOrderDirty;
    private bool _listDragHandlersArmed;
    private bool _exitCleanupStarted;
    private InterventionPopupWindow? _interventionPopup;
    private bool _interventionPopupClosing;
    private readonly List<HelpTopicEntry> _helpTopics = new();
    private readonly List<FileSystemWatcher> _helpWatchers = new();
    private bool _helpIndexUpdating;
    private string? _loadedHelpPath;
    private DateTime _loadedHelpUtc;
    private Timer? _helpReloadTimer;
    private DispatcherTimer? _stressBarPulseTimer;
    private double _stressBarPulsePhase;
    private DispatcherTimer? _hailMaryPulseTimer;
    private double _hailMaryPulsePhase;

    static MainWindow()
    {
        // Class handlers fire before instance handlers, so these stop scroll-into-view
        // from jumping the page when a ListBox row is selected or a control is focused.
        RequestBringIntoViewEvent.AddClassHandler<ScrollViewer>((_, e) => e.Handled = true);
        RequestBringIntoViewEvent.AddClassHandler<ListBoxItem>((_, e) => e.Handled = true);
        InputElement.PointerPressedEvent.AddClassHandler<Button>(OnHeaderLockButtonPointerPressed, handledEventsToo: true);
    }

    public MainWindow()
    {
        InitializeComponent();
        HelpUiNotes.EnsureFromDisk();
        _uiScaleTransform = this.FindControl<LayoutTransformControl>("LayoutScaler")
            ?.LayoutTransform as ScaleTransform;

        DataContextChanged += OnDataContextChanged;
        Opened += OnWindowOpened;
        Closing += OnWindowClosing;
        AddHandler(PointerPressedEvent, OnUserInterfaceActivity, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnUserInterfaceActivity, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnAbortHotkey, RoutingStrategies.Tunnel, handledEventsToo: true);
        var profileScroll = this.FindControl<ScrollViewer>("ModelProfilesScroll");
        profileScroll?.AddHandler(
            Control.RequestBringIntoViewEvent,
            (_, e) => e.Handled = true,
            RoutingStrategies.Bubble | RoutingStrategies.Tunnel,
            handledEventsToo: true);
        var chooser = this.FindControl<ComboBox>("RecoveryReplacementChooser");
        if (chooser is not null)
        {
            chooser.DropDownOpened += (_, _) =>
            {
                if (DataContext is MainViewModel vm)
                {
                    vm.RecoveryChooserDropDownOpen = true;
                }
            };
            chooser.DropDownClosed += (_, _) =>
            {
                if (DataContext is MainViewModel vm)
                {
                    vm.RecoveryChooserDropDownOpen = false;
                }
            };
        }
    }

    private void OnUserInterfaceActivity(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.NoteUserInterfaceActivity();
        }
    }

    /// <summary>
    /// Global "abort" hotkey: Ctrl+Shift+K kills the local model process tree immediately.
    /// This is the fast-escape for a RAM/SSD thrash — it works as long as the AI-FluxMux
    /// window is focused and the process is alive, even if the UI is sluggish.
    /// </summary>
    private void OnAbortHotkey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.K)
        {
            return;
        }

        var modifiers = e.KeyModifiers;
        if ((modifiers & KeyModifiers.Control) == 0
            || (modifiers & KeyModifiers.Shift) == 0
            || (modifiers & KeyModifiers.Alt) != 0)
        {
            return;
        }

        if (DataContext is MainViewModel vm)
        {
            e.Handled = true;
            _ = vm.KillLocalModelNowCommand.ExecuteAsync(null);
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        if (_exitCleanupStarted)
        {
            return;
        }

        // Final save: persist settings now so they survive even if the exit path is
        // interrupted (crash, bluescreen). The rolling .prev backup in the config
        // service protects against the live file being corrupted by this write.
        vm.PersistSettingsForExit();

        e.Cancel = true;
        _exitCleanupStarted = true;
        CloseInterventionPopup();
        Hide();
        _ = FinishExitCleanupAsync(vm);
    }

    private async Task FinishExitCleanupAsync(MainViewModel vm)
    {
        try
        {
            var cleanup = Task.Run(vm.ShutdownManagedRuntimesForExit);
            await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(8)));
        }
        catch
        {
        }

        StopHelpFileWatchers();
        Environment.Exit(0);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        vm.ApplySavedWindowPreferences();

        ApplySavedGeometry(vm);

        ApplyScale(vm.UiScaleFactor);
        vm.PropertyChanged += OnVmPropertyChanged;
        vm.ProfileExpanderRevealRequested += OnProfileExpanderRevealRequested;

        PositionChanged += (_, _) => SaveGeometry(vm);
        SizeChanged   += (_, _) => SaveGeometry(vm);
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        StartupCrashLog.MarkWindowShown();
        if (DataContext is MainViewModel vm)
        {
            ApplySavedGeometry(vm);
            SyncInterventionPopup();
            ApplyGraphTerminalDefaultSplit();
            _ = vm.AutoLoadSelectedProfileOnStartupAsync();
        }
        _stressBarPulseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _stressBarPulseTimer.Tick += OnStressBarPulseTick;
        _stressBarPulseTimer.Start();
    }

    private void ApplySavedGeometry(MainViewModel vm)
    {
        var (x, y, w, h) = vm.LoadWindowGeometry();
        Width = w;
        Height = h;
        if (x == int.MinValue || y == int.MinValue)
        {
            return;
        }

        var working = Screens.All
            .Select(screen => new ScreenBox(screen.WorkingArea.X, screen.WorkingArea.Y, screen.WorkingArea.Width, screen.WorkingArea.Height))
            .ToList();
        var proposed = new ScreenBox(x, y, (int)w, (int)h);
        if (WindowPlacement.HasUsableOnScreenArea(proposed, working))
        {
            Position = new PixelPoint(x, y);
        }
    }

    private void OnStressBarPulseTick(object? sender, EventArgs e)
    {
        _stressBarPulsePhase += 0.05;
        if (_stressBarPulsePhase > Math.PI * 2.0)
        {
            _stressBarPulsePhase -= Math.PI * 2.0;
        }
        var opacity = 0.3 + 0.15 * Math.Sin(_stressBarPulsePhase);
        var path = this.FindControl<global::Avalonia.Controls.Shapes.Path>("TtftTestingBarPath");
        if (path is { IsVisible: true })
        {
            path.Opacity = opacity;
        }
        var path2 = this.FindControl<global::Avalonia.Controls.Shapes.Path>("ThroughputTestingBarPath");
        if (path2 is { IsVisible: true })
        {
            path2.Opacity = opacity;
        }
        // Drive the scroll animation FIRST so the bars, bar value labels, and X-axis labels
        // are all positioned based on the same (newest) scroll offset. Previously the labels
        // were positioned before the scroll advanced, causing them to lag one frame behind
        // the bars during the smooth right-to-left glide.
        DriveScrollAnimation();
        PositionXAxisLabels();
        PositionYAxisLabels();
        PositionBarValueLabels();
    }

    private void StartHailMaryPulse()
    {
        if (_hailMaryPulseTimer is not null)
        {
            return;
        }

        _hailMaryPulseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };
        _hailMaryPulseTimer.Tick += OnHailMaryPulseTick;
        _hailMaryPulseTimer.Start();
    }

    private void StopHailMaryPulse()
    {
        _hailMaryPulseTimer?.Stop();
        _hailMaryPulseTimer = null;
        if (HailMaryButton is not null)
        {
            HailMaryButton.Opacity = 1.0;
        }
    }

    private void OnHailMaryPulseTick(object? sender, EventArgs e)
    {
        _hailMaryPulsePhase += 0.1;
        if (_hailMaryPulsePhase > Math.PI * 2.0)
        {
            _hailMaryPulsePhase -= Math.PI * 2.0;
        }

        if (HailMaryButton is { IsVisible: true })
        {
            HailMaryButton.Opacity = 0.5 + 0.5 * Math.Sin(_hailMaryPulsePhase);
        }
    }

    /// <summary>
    /// Toggles the Hail Mary picker popup on the Diagnostics bar. Clicking the button opens
    /// the picker (if closed) or closes it (if open). The picker's radio buttons / checkbox
    /// update the ViewModel bindings; arming only happens on the "Armed" button.
    /// </summary>
    private void HailMaryButton_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (HailMaryPickerPopup is not null)
        {
            HailMaryPickerPopup.IsOpen = !HailMaryPickerPopup.IsOpen;
        }
    }

    /// <summary>
    /// Closes the Hail Mary picker popup after the user arms the prune (the "Armed" button
    /// already ran the command; this just dismisses the popup).
    /// </summary>
    private void HailMaryPickerArm_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (HailMaryPickerPopup is not null)
        {
            HailMaryPickerPopup.IsOpen = false;
        }
    }

    /// <summary>
    /// Closes the Hail Mary picker popup without arming the prune. The picker's radio buttons /
    /// checkbox already updated the ViewModel bindings, so the choice is retained for next time
    /// the popup opens (it is still one-off — arming only happens on the "Armed" button).
    /// </summary>
    private void HailMaryPickerCancel_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (HailMaryPickerPopup is not null)
        {
            HailMaryPickerPopup.IsOpen = false;
        }
    }

    /// <summary>
    /// Drives the smooth right-to-left scroll of the live per-turn telemetry graphs. For each
    /// graph in scroll mode, computes the eased scroll offset from the time elapsed since its
    /// last turn was plotted and advances the sliding window accordingly. The easing is a
    /// smoothstep over ~1.2s: the newest bar starts at the right edge and glides left by one slot
    /// as the offset reaches 1, so new bars appear to the right of the most-recent bar and old
    /// bars (with their turn numbers and value labels) exit off the left edge. No-op for graphs
    /// not in scroll mode (the stress test and idle graphs are untouched).
    /// </summary>
    private void DriveScrollAnimation()
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        AdvanceScroll(vm.TelemetryGraph);
        AdvanceScroll(vm.TelemetryGraphThroughput);
    }

    private static void AdvanceScroll(TelemetryGraphViewModel graph)
    {
        if (!graph.IsScrollMode || graph.LastPointAddedUtc == DateTime.MinValue)
        {
            return;
        }

        const double scrollDurationSeconds = 1.2; // time for the window to slide one full slot
        var elapsed = (DateTime.UtcNow - graph.LastPointAddedUtc).TotalSeconds;
        var t = Math.Clamp(elapsed / scrollDurationSeconds, 0.0, 1.0);
        // Linear easing: constant speed from start to finish. The previous smoothstep easing
        // (t*t*(3-2t)) started and ended very slowly (derivative=0 at t=0 and t=1), which
        // caused a visible "flicker" or "pause" just as the bars were about to scroll. A
        // linear glide is smoother and more predictable.
        var offset = t;
        graph.ScrollTo(offset);
    }

    private void PositionYAxisLabels()
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        PositionYAxisLabelSet(
            this.FindControl<Canvas>("TtftYAxisLabelCanvas"),
            this.FindControl<global::Avalonia.Controls.Border>("TtftPlotBorder"),
            new[]
            {
                this.FindControl<TextBlock>("TtftYLabelMax"),
                this.FindControl<TextBlock>("TtftYLabel75"),
                this.FindControl<TextBlock>("TtftYLabel50"),
                this.FindControl<TextBlock>("TtftYLabel25"),
                this.FindControl<TextBlock>("TtftYLabelMin"),
            },
            new[] { vm.TelemetryGraph.YAxisMax, vm.TelemetryGraph.YAxis75, vm.TelemetryGraph.YAxis50, vm.TelemetryGraph.YAxis25, vm.TelemetryGraph.YAxisMin },
            new[]
            {
                this.FindControl<global::Avalonia.Controls.Shapes.Rectangle>("TtftYTick0"),
                this.FindControl<global::Avalonia.Controls.Shapes.Rectangle>("TtftYTick1"),
                this.FindControl<global::Avalonia.Controls.Shapes.Rectangle>("TtftYTick2"),
                this.FindControl<global::Avalonia.Controls.Shapes.Rectangle>("TtftYTick3"),
                this.FindControl<global::Avalonia.Controls.Shapes.Rectangle>("TtftYTick4"),
            },
            vm.TelemetryGraph.IsPlotVisible);

        PositionYAxisLabelSet(
            this.FindControl<Canvas>("ThroughputYAxisLabelCanvas"),
            this.FindControl<global::Avalonia.Controls.Border>("ThroughputPlotBorder"),
            new[]
            {
                this.FindControl<TextBlock>("ThroughputYLabelMax"),
                this.FindControl<TextBlock>("ThroughputYLabel75"),
                this.FindControl<TextBlock>("ThroughputYLabel50"),
                this.FindControl<TextBlock>("ThroughputYLabel25"),
                this.FindControl<TextBlock>("ThroughputYLabelMin"),
            },
            new[] { vm.TelemetryGraphThroughput.YAxisMax, vm.TelemetryGraphThroughput.YAxis75, vm.TelemetryGraphThroughput.YAxis50, vm.TelemetryGraphThroughput.YAxis25, vm.TelemetryGraphThroughput.YAxisMin },
            new[]
            {
                this.FindControl<global::Avalonia.Controls.Shapes.Rectangle>("ThroughputYTick0"),
                this.FindControl<global::Avalonia.Controls.Shapes.Rectangle>("ThroughputYTick1"),
                this.FindControl<global::Avalonia.Controls.Shapes.Rectangle>("ThroughputYTick2"),
                this.FindControl<global::Avalonia.Controls.Shapes.Rectangle>("ThroughputYTick3"),
                this.FindControl<global::Avalonia.Controls.Shapes.Rectangle>("ThroughputYTick4"),
            },
            vm.TelemetryGraphThroughput.IsPlotVisible);
    }

    private static void PositionYAxisLabelSet(
        Canvas? canvas,
        global::Avalonia.Controls.Border? plotBorder,
        TextBlock[] labels,
        string[] texts,
        global::Avalonia.Controls.Shapes.Rectangle[] ticks,
        bool isActive)
    {
        if (canvas is null || plotBorder is null || labels.Length != 5)
        {
            return;
        }

        var cw = canvas.Bounds.Width;
        if (cw <= 0)
        {
            return;
        }

        // Reserve a top + bottom buffer zone (otherwise empty) so the top (0%) and bottom (100%)
        // Y-axis numbers can sit CENTERED on their tick marks without being clipped by the plot's
        // edge. The buffer is derived from the Y-labels' own rendered height (DesiredSize.Height),
        // which already reflects DPI scaling and the active font — so the buffer scales with DPI
        // and font size rather than being a static pixel constant. Half the tallest label's height
        // is exactly enough for a centered label at the top/bottom tick to fit within the buffer.
        double maxLabelHeight = 0;
        foreach (var l in labels)
        {
            if (l is { } lb && lb.DesiredSize.Height > maxLabelHeight)
            {
                maxLabelHeight = lb.DesiredSize.Height;
            }
        }

        // The effective buffer: half the tallest label's height, rounded up. If the labels aren't
        // measured yet (first frame right after IsVisible flips), fall back to the plot border's
        // current top margin so positioning still works with whatever buffer is already applied;
        // the next tick (after layout) measures the labels and applies the correct buffer.
        var currentMargin = plotBorder.Margin;
        var buffer = maxLabelHeight > 0
            ? Math.Ceiling(maxLabelHeight / 2.0)
            : currentMargin.Top;

        // Apply the buffer as a top/bottom margin on the plot border so its drawing area insets,
        // leaving the buffer space above and below within the shared Row 0. Only update the margin
        // when the buffer actually changes (e.g. after a DPI or font-scale change) to avoid a
        // layout pass on every 50 ms timer tick.
        if (maxLabelHeight > 0 && (currentMargin.Top != buffer || currentMargin.Bottom != buffer))
        {
            plotBorder.Margin = new Thickness(currentMargin.Left, buffer, currentMargin.Right, buffer);
        }

        // The Y-axis label canvas fills the full Row 0 (plot + buffers), so its top-left is at the
        // row's top-left. The plot border is inset from the row's top by its top margin (= buffer),
        // so in canvas coordinates the border's outer top edge sits at y = buffer. Control.Bounds
        // EXCLUDES the margin, so plotBounds.Height is the border's rendered height (row height
        // minus the top+bottom margins) — the buffer is already accounted for and must NOT be
        // subtracted again. The inner drawing area is inset from the border's outer top by
        // BorderThickness.Top.
        var plotBounds = plotBorder.Bounds;
        var borderThickness = plotBorder.BorderThickness;
        var innerTop = buffer + borderThickness.Top;
        var innerHeight = plotBounds.Height - borderThickness.Top - borderThickness.Bottom;
        if (innerHeight <= 0)
        {
            return;
        }

        var fractions = new[] { 0.0, 0.25, 0.50, 0.75, 1.0 };
        const double gap = 6.0; // gap between the tick's outer edge and the number (ticks are 4px wide)

        for (int i = 0; i < 5; i++)
        {
            if (labels[i] is not { } label) continue;

            label.IsVisible = isActive;
            if (!isActive) continue;

            label.Text = texts[i];
            var centerY = innerTop + fractions[i] * innerHeight;
            var labelHeight = label.DesiredSize.Height;
            // Center the label on its tick mark. The buffer guarantees the top (0%) and bottom
            // (100%) labels — centered on ticks at the inset drawing area's edges — fit within the
            // buffer zone, so no clamping is needed and the numbers stay aligned with the ticks.
            var top = centerY - labelHeight / 2.0;
            var labelWidth = label.DesiredSize.Width;
            var left = cw - gap - labelWidth;
            if (left < 0) left = 0;
            Canvas.SetTop(label, top);
            Canvas.SetLeft(label, left);

            if (ticks[i] is { } tick)
            {
                tick.IsVisible = isActive;
                var tickY = innerTop + fractions[i] * innerHeight - 0.5;
                Canvas.SetTop(tick, tickY);
                var tickWidth = tick.DesiredSize.Width > 0 ? tick.DesiredSize.Width : 4.0;
                Canvas.SetLeft(tick, cw - tickWidth);
            }
        }
    }

    private void PositionXAxisLabels()
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        PositionAxisLabelSet(
            this.FindControl<Canvas>("TtftXAxisLabelCanvas"),
            new[]
            {
                this.FindControl<TextBlock>("TtftXLabel1"),
                this.FindControl<TextBlock>("TtftXLabel2"),
                this.FindControl<TextBlock>("TtftXLabel3"),
                this.FindControl<TextBlock>("TtftXLabel4"),
                this.FindControl<TextBlock>("TtftXLabel5"),
                this.FindControl<TextBlock>("TtftXLabel6"),
                this.FindControl<TextBlock>("TtftXLabel7"),
                this.FindControl<TextBlock>("TtftXLabel8"),
                this.FindControl<TextBlock>("TtftXLabel9"),
                this.FindControl<TextBlock>("TtftXLabel10"),
                this.FindControl<TextBlock>("TtftXLabel11"),
                this.FindControl<TextBlock>("TtftXLabel12"),
                this.FindControl<TextBlock>("TtftXLabel13"),
                this.FindControl<TextBlock>("TtftXLabel14"),
                this.FindControl<TextBlock>("TtftXLabel15"),
                this.FindControl<TextBlock>("TtftXLabel16"),
            },
            vm.TelemetryGraph.XAxisLabels);

        PositionAxisLabelSet(
            this.FindControl<Canvas>("ThroughputXAxisLabelCanvas"),
            new[]
            {
                this.FindControl<TextBlock>("ThroughputXLabel1"),
                this.FindControl<TextBlock>("ThroughputXLabel2"),
                this.FindControl<TextBlock>("ThroughputXLabel3"),
                this.FindControl<TextBlock>("ThroughputXLabel4"),
                this.FindControl<TextBlock>("ThroughputXLabel5"),
                this.FindControl<TextBlock>("ThroughputXLabel6"),
                this.FindControl<TextBlock>("ThroughputXLabel7"),
                this.FindControl<TextBlock>("ThroughputXLabel8"),
                this.FindControl<TextBlock>("ThroughputXLabel9"),
                this.FindControl<TextBlock>("ThroughputXLabel10"),
                this.FindControl<TextBlock>("ThroughputXLabel11"),
                this.FindControl<TextBlock>("ThroughputXLabel12"),
                this.FindControl<TextBlock>("ThroughputXLabel13"),
                this.FindControl<TextBlock>("ThroughputXLabel14"),
                this.FindControl<TextBlock>("ThroughputXLabel15"),
                this.FindControl<TextBlock>("ThroughputXLabel16"),
            },
            vm.TelemetryGraphThroughput.XAxisLabels);
    }

    private static void PositionAxisLabelSet(
        Canvas? canvas,
        TextBlock[] labels,
        System.Collections.ObjectModel.ObservableCollection<TelemetryGraphViewModel.XAxisLabelItem> axisLabels)
    {
        if (canvas is null || axisLabels is null)
        {
            return;
        }

        var cw = canvas.Bounds.Width;
        if (cw <= 0)
        {
            return;
        }

        for (int i = 0; i < labels.Length; i++)
        {
            if (labels[i] is not { } label) continue;

            if (i >= axisLabels.Count)
            {
                label.IsVisible = false;
                continue;
            }

            var item = axisLabels[i];
            if (string.IsNullOrEmpty(item.Text))
            {
                label.IsVisible = false;
                continue;
            }

            label.Text = item.Text;
            label.IsVisible = true;
            var x = item.Position / 100.0 * cw;
            var labelWidth = label.DesiredSize.Width;
            double left;
            if (item.IsAxisMax)
            {
                // Right-align the axis-max (end) label: its right edge sits at the axis end instead
                // of centering under the tick. This pushes the "end" label flush against the right
                // edge of the plot so it reads as the axis terminus.
                left = x - labelWidth;
                if (left < 0) left = 0;
            }
            else
            {
                left = x - labelWidth / 2.0;
                if (left < 0) left = 0;
                if (left + labelWidth > cw) left = cw - labelWidth;
            }
            Canvas.SetLeft(label, left);
        }
    }

    private void PositionBarValueLabels()
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        PositionBarValueLabelSet(
            this.FindControl<Canvas>("TtftBarValueLabelCanvas"),
            new[]
            {
                this.FindControl<TextBlock>("TtftBarValue1"),
                this.FindControl<TextBlock>("TtftBarValue2"),
                this.FindControl<TextBlock>("TtftBarValue3"),
                this.FindControl<TextBlock>("TtftBarValue4"),
                this.FindControl<TextBlock>("TtftBarValue5"),
                this.FindControl<TextBlock>("TtftBarValue6"),
            },
            vm.TelemetryGraph);

        PositionBarValueLabelSet(
            this.FindControl<Canvas>("ThroughputBarValueLabelCanvas"),
            new[]
            {
                this.FindControl<TextBlock>("ThroughputBarValue1"),
                this.FindControl<TextBlock>("ThroughputBarValue2"),
                this.FindControl<TextBlock>("ThroughputBarValue3"),
                this.FindControl<TextBlock>("ThroughputBarValue4"),
                this.FindControl<TextBlock>("ThroughputBarValue5"),
                this.FindControl<TextBlock>("ThroughputBarValue6"),
            },
            vm.TelemetryGraphThroughput);
    }

    private static void PositionBarValueLabelSet(
        Canvas? canvas,
        TextBlock[] labels,
        TelemetryGraphViewModel graph)
    {
        if (canvas is null || labels.Length != 6)
        {
            return;
        }

        var cw = canvas.Bounds.Width;
        var ch = canvas.Bounds.Height;
        if (cw <= 0 || ch <= 0)
        {
            return;
        }

        // Use the bar's own center X (BarValueX1-6), NOT the axis tick positions (XAxisPos1-6).
        // The axis ticks are evenly spaced, but the bars sit at the actual data-point X values.
        // In the per-turn view that's turn indices spaced by the padded range, so the axis-tick X
        // would offset each value label from its bar. BarValueX keeps each label centered on its
        // own bar. In the stress test the bars are evenly spaced at the fill levels, so
        // BarValueX == XAxisPos and this is a no-op there (stress-test alignment unchanged).
        var xPositions = new[] { graph.BarValueX1, graph.BarValueX2, graph.BarValueX3, graph.BarValueX4, graph.BarValueX5, graph.BarValueX6 };
        var yPositions = new[] { graph.BarValueY1, graph.BarValueY2, graph.BarValueY3, graph.BarValueY4, graph.BarValueY5, graph.BarValueY6 };
        var texts = new[] { graph.BarValueText1, graph.BarValueText2, graph.BarValueText3, graph.BarValueText4, graph.BarValueText5, graph.BarValueText6 };

        // Compute the label headroom fraction: the fraction of the chart height reserved for
        // the tallest bar's value label. This is the tallest label's height (plus a 2px gap)
        // divided by the chart height. The Y-axis rescales to ensure this headroom is available
        // above the tallest bar, so the label is never forced to overlap the bar or the top.
        // The headroom is applied in the correct space (linear or log) by GetEffectiveYAxisRange.
        // For log-scale graphs, the log compression pushes the tallest bar closer to the top,
        // so use a larger headroom (3x) to account for this.
        double maxLabelHeight = 0;
        foreach (var l in labels)
        {
            if (l is { } lb && lb.DesiredSize.Height > maxLabelHeight)
            {
                maxLabelHeight = lb.DesiredSize.Height;
            }
        }
        var baseHeadroom = (maxLabelHeight + 2.0) / ch;
        var headroomFraction = baseHeadroom;
        graph.SetLabelHeadroomFraction(headroomFraction);

        for (int i = 0; i < 6; i++)
        {
            if (labels[i] is not { } label) continue;

            if (string.IsNullOrEmpty(texts[i]))
            {
                // The label's IsVisible is controlled by the XAML binding (StringToVisibilityConverter),
                // so no need to set it here. Just skip positioning.
                continue;
            }

            var x = xPositions[i] / 100.0 * cw;
            var y = yPositions[i] / 100.0 * ch;
            var labelWidth = label.DesiredSize.Width;
            var labelHeight = label.DesiredSize.Height;
            var left = x - labelWidth / 2.0;
            var top = y - labelHeight - 2.0;
            if (left < 0) left = 0;
            if (left + labelWidth > cw) left = cw - labelWidth;
            if (top < 0) top = 0;
            Canvas.SetLeft(label, left);
            Canvas.SetTop(label, top);
        }
    }

    /// <summary>
    /// Resets the diagnostics panel row height to the bound <c>DiagnosticsRowHeight</c> value.
    /// The GridSplitter at Row 2 can override the RowDefinition's Height with a pixel value
    /// (breaking the binding), so when the stress test starts we clear that override and
    /// re-apply the star-based height so the panel expands correctly.
    /// </summary>
    public void ResetDiagnosticsPanelHeight()
    {
        var grid = this.FindControl<Grid>("MainLayoutGrid");
        if (grid is null || grid.RowDefinitions.Count < 4)
        {
            return;
        }

        var rowDef = grid.RowDefinitions[3];
        // Clear any pixel-based height set by the GridSplitter, then re-apply the binding.
        rowDef.Height = new GridLength(1.0, GridUnitType.Star); // temporary to break the pixel override
        if (DataContext is MainViewModel vm)
        {
            rowDef.Height = vm.DiagnosticsRowHeight;
        }
    }

    private void ApplyGraphTerminalDefaultSplit()
    {
        var grid = this.FindControl<Grid>("GraphTerminalGrid");
        if (grid is null)
        {
            return;
        }

        var applied = false;
        void OnLayoutUpdated(object? s, EventArgs args)
        {
            if (!applied && grid.Bounds.Width > 0)
            {
                applied = true;
                grid.LayoutUpdated -= OnLayoutUpdated;
                var value = grid.Bounds.Width * 0.33;
                grid.ColumnDefinitions[0].Width = new GridLength(value, GridUnitType.Pixel);
                grid.ColumnDefinitions[2].Width = new GridLength(1.0, GridUnitType.Star);
            }
        }
        grid.LayoutUpdated += OnLayoutUpdated;
    }

    private void OnStressTestButtonClick(object? sender, RoutedEventArgs e)
    {
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.UiScaleFactor) && sender is MainViewModel vm)
        {
            ApplyScale(vm.UiScaleFactor);
        }

        if (e.PropertyName == nameof(MainViewModel.EnforceCleanWidth))
        {
            UpdateMinWidthForSelectedTab(this.FindControl<TabControl>("MainTabs"));
        }

        if (e.PropertyName == nameof(MainViewModel.SelectedUiTheme))
        {
            ApplyThemeToOwnedOverlays();
        }

        if (e.PropertyName is nameof(MainViewModel.InterventionPopupsEnabled)
            or nameof(MainViewModel.CloudRecommendVisible)
            or nameof(MainViewModel.LocalReloadRecommendVisible))
        {
            SyncInterventionPopup();
        }

        if (e.PropertyName == nameof(MainViewModel.SelectedMainTabIndex) && IsHelpTabSelected())
        {
            ReloadHelpFromFile(force: false);
        }

        if (e.PropertyName == nameof(MainViewModel.HailMaryQueued) && sender is MainViewModel hailVm)
        {
            if (hailVm.HailMaryQueued)
            {
                StartHailMaryPulse();
            }
            else
            {
                StopHailMaryPulse();
            }
        }

        // Note: The diagnostics panel now defaults to 1.8* (doubled from the old 0.45*), so
        // there's no need to reset its height when the stress test starts. The panel is always
        // large enough to show the live telemetry graph.

        if (e.PropertyName == nameof(MainViewModel.DiagnosticTerminalText))
        {
            Dispatcher.UIThread.Post(() =>
            {
                var terminal = this.FindControl<TextBox>("DiagnosticTerminal");
                if (terminal is null)
                {
                    return;
                }

                if (terminal.IsFocused)
                {
                    terminal.CaretIndex = terminal.Text?.Length ?? 0;
                    return;
                }

                var inner = terminal.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                if (inner is not null)
                {
                    inner.Offset = new Vector(inner.Offset.X, Math.Max(0, inner.Extent.Height - inner.Viewport.Height));
                }
            });
        }
    }

    private void SyncInterventionPopup()
    {
        if (DataContext is not MainViewModel vm || _exitCleanupStarted)
        {
            return;
        }

        var needsDecision = vm.InterventionPopupsEnabled
            && (vm.CloudRecommendVisible || vm.LocalReloadRecommendVisible);
        if (!needsDecision)
        {
            Dispatcher.UIThread.Post(CloseInterventionPopupIfStillIdle, DispatcherPriority.ContextIdle);
            return;
        }

        if (_interventionPopup is null)
        {
            _interventionPopup = new InterventionPopupWindow
            {
                DataContext = vm,
                Topmost = true,
                ShowInTaskbar = true,
                RequestedThemeVariant = Application.Current?.RequestedThemeVariant
            };
            _interventionPopup.Closing += OnInterventionPopupClosing;
            _interventionPopup.Closed += OnInterventionPopupClosed;
            _interventionPopup.Show();
        }
    }

    private void OnInterventionPopupClosing(object? sender, WindowClosingEventArgs e)
    {
        if (sender is Window popup)
        {
            popup.Topmost = false;
        }
    }

    private void OnInterventionPopupClosed(object? sender, EventArgs e)
    {
        if (_interventionPopupClosing)
        {
            return;
        }

        if (_interventionPopup is not null)
        {
            _interventionPopup.Closing -= OnInterventionPopupClosing;
            _interventionPopup.Closed -= OnInterventionPopupClosed;
            _interventionPopup = null;
        }

        if (DataContext is MainViewModel vm)
        {
            vm.DismissPendingIntervention();
        }
    }

    private void CloseInterventionPopupIfStillIdle()
    {
        if (DataContext is MainViewModel vm
            && vm.InterventionPopupsEnabled
            && (vm.CloudRecommendVisible || vm.LocalReloadRecommendVisible))
        {
            return;
        }

        CloseInterventionPopup();
    }

    private void CloseInterventionPopup()
    {
        var popup = _interventionPopup;
        if (popup is null)
        {
            return;
        }

        _interventionPopup = null;
        _interventionPopupClosing = true;
        popup.Closing -= OnInterventionPopupClosing;
        popup.Closed -= OnInterventionPopupClosed;
        try
        {
            popup.Topmost = false;
            popup.Hide();
        }
        catch
        {
        }

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                popup.Close();
            }
            catch
            {
            }
            finally
            {
                _interventionPopupClosing = false;
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    private void OnProfileExpanderRevealRequested(CloudVariantTreeItemViewModel item, bool focusNameBox)
        => ProfileExpanderReveal.Schedule(this, item, focusNameBox);

    private void GpuVramSectionToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.GpuVramSectionExpanded = !vm.GpuVramSectionExpanded;
            if (vm.GpuVramSectionExpanded)
            {
                _ = vm.LoadGpuVramFlyoutCommand.ExecuteAsync(null);
            }
        }
    }

    private void OnGpuAppToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.DataContext is not GpuAppListItem item)
        {
            return;
        }

        // After the Click event, IsChecked reflects the new state the user selected.
        // IsOnMotherboardGpu in the item is the OLD state (before the toggle).
        // If the checkbox is now checked, the user wants Motherboard GPU.
        // If unchecked, the user wants Discrete GPU.
        var targetIsMotherboard = checkBox.IsChecked == true;

        // Only act if the state actually changed.
        if (targetIsMotherboard == item.IsOnMotherboardGpu)
        {
            return;
        }

        _ = ToggleGpuAppAsync(item, targetIsMotherboard);
    }

    private async Task ToggleGpuAppAsync(GpuAppListItem item, bool toMotherboard)
    {
        if (!item.CanToggle)
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var target = toMotherboard
            ? WindowsGpuPreference.PowerSaving
            : WindowsGpuPreference.HighPerformance;

        string? statusMsg;
        try
        {
            var success = await Task.Run(() => WindowsGpuPreference.ApplyForApp(item.AppName, target)).ConfigureAwait(true);
            statusMsg = success
                ? $"{item.DisplayName} moved to {WindowsGpuPreference.LabelFor(target)}."
                : $"Could not change GPU affinity for {item.DisplayName}.";
        }
        catch (Exception ex)
        {
            statusMsg = $"Could not change GPU affinity for {item.DisplayName}. {ex.Message}";
        }

        if (DataContext is MainViewModel mainVm)
        {
            mainVm.StatusMessage = statusMsg;
            mainVm.RefreshGpuAppRowsForUi();
        }
    }

    private void ApplyThemeToOwnedOverlays()
    {
        var variant = Application.Current?.RequestedThemeVariant;
        if (_interventionPopup is not null)
        {
            _interventionPopup.RequestedThemeVariant = variant;
        }
    }

    private void VramProcessGpuToggle_Checked(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.Tag is not string appName)
        {
            return;
        }

        // Apply the GPU affinity change to Discrete GPU (high performance).
        var success = Services.WindowsGpuPreference.ApplyForApp(appName, Services.WindowsGpuPreference.HighPerformance);
        if (success)
        {
            // Show a status message.
            if (DataContext is MainViewModel vm)
            {
                vm.StatusMessage = $"GPU affinity for {appName} changed to Discrete GPU. Applied after Windows restart.";
            }
        }
        else
        {
            // Revert the checkbox if the change failed.
            checkBox.IsChecked = false;
            // Show a status message.
            if (DataContext is MainViewModel vm)
            {
                vm.StatusMessage = $"Could not change GPU affinity for {appName}. The app may be pinned.";
            }
        }
    }

    private void VramProcessGpuToggle_Unchecked(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.Tag is not string appName)
        {
            return;
        }

        // Apply the GPU affinity change to Motherboard GPU (power saving).
        var success = Services.WindowsGpuPreference.ApplyForApp(appName, Services.WindowsGpuPreference.PowerSaving);
        if (success)
        {
            // Show a status message.
            if (DataContext is MainViewModel vm)
            {
                vm.StatusMessage = $"GPU affinity for {appName} changed to Motherboard GPU. Applied after Windows restart.";
            }
        }
        else
        {
            // Revert the checkbox if the change failed.
            checkBox.IsChecked = true;
            // Show a status message.
            if (DataContext is MainViewModel vm)
            {
                vm.StatusMessage = $"Could not change GPU affinity for {appName}. The app may be pinned.";
            }
        }
    }

    private void VramProcessGpuToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.Tag is not string appName)
        {
            return;
        }

        // Get the current state of the checkbox
        bool isChecked = checkBox.IsChecked == true;

        if (isChecked)
        {
            // Apply the GPU affinity change to Discrete GPU (high performance).
            var success = Services.WindowsGpuPreference.ApplyForApp(appName, Services.WindowsGpuPreference.HighPerformance);
            if (success)
            {
                // Show a status message.
                if (DataContext is MainViewModel vm)
                {
                    vm.StatusMessage = $"GPU affinity for {appName} changed to Discrete GPU.";
                }
            }
            else
            {
                // Revert the checkbox if the change failed.
                checkBox.IsChecked = false;
                // Show a status message.
                if (DataContext is MainViewModel vm)
                {
                    vm.StatusMessage = $"Could not change GPU affinity for {appName}. The app may be pinned.";
                }
            }
        }
        else
        {
            // Apply the GPU affinity change to Motherboard GPU (power saving).
            var success = Services.WindowsGpuPreference.ApplyForApp(appName, Services.WindowsGpuPreference.PowerSaving);
            if (success)
            {
                // Show a status message.
                if (DataContext is MainViewModel vm)
                {
                    vm.StatusMessage = $"GPU affinity for {appName} changed to Motherboard GPU. Applied after Windows restart.";
                }
            }
            else
            {
                // Revert the checkbox if the change failed.
                checkBox.IsChecked = true;
                // Show a status message.
                if (DataContext is MainViewModel vm)
                {
                    vm.StatusMessage = $"Could not change GPU affinity for {appName}. The app may be pinned.";
                }
            }
        }
    }

    private void ApplyScale(double factor)
    {
        if (_uiScaleTransform is null)
            return;
        _uiScaleTransform.ScaleX = factor;
        _uiScaleTransform.ScaleY = factor;

        // Update the window's minimum width based on the selected tab and the new scale factor.
        UpdateMinWidthForSelectedTab(this.FindControl<TabControl>("MainTabs"));
    }

    private void HeaderChrome_LayoutUpdated(object? sender, EventArgs e)
    {
        var spacer = this.FindControl<Control>("CaptionButtonSpacer");
        if (spacer is null)
        {
            return;
        }

        var right = WindowDecorationMargin.Right;
        spacer.Width = right > 1 ? right : 138;
    }

    private void SaveGeometry(MainViewModel vm)
    {
        if (WindowState != WindowState.Normal)
            return;
        vm.SaveWindowGeometry(Position.X, Position.Y, (int)Width, (int)Height);
    }

    private static void OnHeaderLockButtonPointerPressed(Button button, PointerPressedEventArgs e)
    {
        if (!button.Classes.Contains("headerLockButton")
            || !e.GetCurrentPoint(button).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        var profile = button.DataContext as CloudVariantTreeItemViewModel;
        if (profile is null)
        {
            for (Visual? visual = button; visual is not null; visual = visual.GetVisualParent())
            {
                if (visual is Expander expander)
                {
                    profile = expander.DataContext as CloudVariantTreeItemViewModel;
                    break;
                }

                if (visual is StyledElement { DataContext: CloudVariantTreeItemViewModel fromAncestor })
                {
                    profile = fromAncestor;
                    break;
                }
            }
        }

        if (profile is null)
        {
            return;
        }

        var window = TopLevel.GetTopLevel(button) as MainWindow
                     ?? button.FindAncestorOfType<MainWindow>();
        if (window?.DataContext is not MainViewModel vm)
        {
            return;
        }

        if (vm.ToggleProfileDefaultLockCommand is IAsyncRelayCommand asyncCommand)
        {
            _ = asyncCommand.ExecuteAsync(profile);
            return;
        }

        vm.ToggleProfileDefaultLockCommand.Execute(profile);
    }

    private void VariantExpander_Expanded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Expander { DataContext: CloudVariantTreeItemViewModel item } ||
            DataContext is not MainViewModel vm)
            return;

        vm.OnAdvancedProfileExpanderExpanded(item);
        if (!vm.SuppressProfileExpanderReveal)
        {
            ProfileExpanderReveal.Schedule(this, item, focusNameBox: false);
        }
    }

    private void VariantExpander_Collapsing(object? sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander)
        {
            return;
        }

        if (ProfileExpanderReveal.TryDeferCollapse(expander))
        {
            e.Handled = true;
            if (e is CancelRoutedEventArgs cancel)
            {
                cancel.Cancel = true;
            }
        }
    }

    private void SuppressProfileListSelection(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    private void OnQuickSelectSlotGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control grip || !e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var slot = grip.DataContext as RouteSlotViewModel
            ?? grip.FindAncestorOfType<GroupBox>()?.DataContext as RouteSlotViewModel;
        if (slot is null)
        {
            return;
        }

        _draggedQuickSelectSlot = slot;
        _quickSelectOrderDirty = false;
        if (QuickSelectSlotList is not null)
        {
            ReorderMoveAnimation.BeginFollow(QuickSelectSlotList, slot, e);
        }

        ArmListDragHandlers();
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnQuickSelectSlotGripMoved(object? sender, PointerEventArgs e)
        => ContinueQuickSelectDrag(e);

    private void OnQuickSelectSlotGripReleased(object? sender, PointerReleasedEventArgs e)
        => FinishQuickSelectSlotDrag();

    private void OnQuickSelectSlotGripCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
    }

    private void ContinueQuickSelectDrag(PointerEventArgs e)
    {
        if (_draggedQuickSelectSlot is null
            || DataContext is not MainViewModel vm
            || QuickSelectSlotList is null
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        ReorderMoveAnimation.Follow(QuickSelectSlotList, _draggedQuickSelectSlot, e);

        var moved = false;
        for (var step = 0; step < QuickSelectSlotList.ItemCount; step++)
        {
            var target = ReorderMoveAnimation.GetDisplacementTarget(QuickSelectSlotList, _draggedQuickSelectSlot)
                as RouteSlotViewModel;
            if (target is null || ReferenceEquals(target, _draggedQuickSelectSlot))
            {
                break;
            }

            vm.ReorderQuickSelectSlot(_draggedQuickSelectSlot, target);
            moved = true;
        }

        if (moved)
        {
            _quickSelectOrderDirty = true;
        }

        e.Handled = true;
    }

    private void FinishQuickSelectSlotDrag()
    {
        DisarmListDragHandlers();
        if (_quickSelectOrderDirty && DataContext is MainViewModel vm)
        {
            vm.PersistQuickSelectSlotOrder();
        }

        if (QuickSelectSlotList is not null)
        {
            ReorderMoveAnimation.EndFollow(QuickSelectSlotList, _draggedQuickSelectSlot);
        }

        _draggedQuickSelectSlot = null;
        _quickSelectOrderDirty = false;
    }

    private static object? GetItemAtPointer(ItemsControl list, PointerEventArgs e)
    {
        var index = GetItemIndexAtPointer(list, e);
        if (index is null)
        {
            return null;
        }

        return list.ContainerFromIndex(index.Value) is Control container
            ? container.DataContext ?? list.Items[index.Value]
            : list.Items[index.Value];
    }

    private static int? GetItemIndexAtPointer(ItemsControl list, PointerEventArgs e)
    {
        Visual? parent = null;
        for (var index = 0; index < list.ItemCount; index++)
        {
            if (list.ContainerFromIndex(index) is Control item && item.Parent is Visual visual)
            {
                parent = visual;
                break;
            }
        }

        if (parent is null)
        {
            return null;
        }

        var point = e.GetPosition(parent);
        var nearest = -1;
        var nearestDistance = double.MaxValue;
        for (var index = 0; index < list.ItemCount; index++)
        {
            if (list.ContainerFromIndex(index) is not Control item)
            {
                continue;
            }

            var bounds = item.Bounds;
            if (point.Y >= bounds.Top && point.Y <= bounds.Bottom)
            {
                return index;
            }

            var distance = Math.Abs(point.Y - ((bounds.Top + bounds.Bottom) / 2));
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = index;
            }
        }

        return nearest >= 0 ? nearest : null;
    }

    private void PriorityListBox_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (FindPriorityListBox(sender) is not { } listBox
            || !e.GetCurrentPoint(listBox).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var selectedPriority = GetItemAtPointer(listBox, e) as string
            ?? (sender as Control)?.DataContext as string;
        if (selectedPriority is null)
        {
            return;
        }

        listBox.SelectedItem = selectedPriority;
        _draggedPriorityName = selectedPriority;
        _draggedPriorityList = listBox;
        _draggedPriorityCollection = listBox.ItemsSource == (DataContext as MainViewModel)?.CloudPriorityOrder
            ? "cloud"
            : "local";
        ReorderMoveAnimation.BeginFollow(listBox, selectedPriority, e);
        ArmListDragHandlers();
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void PriorityListBox_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (FindPriorityListBox(sender) is { } listBox)
        {
            ContinuePriorityDrag(listBox, e);
        }
    }

    private void PriorityListBox_PointerReleased(object? sender, PointerReleasedEventArgs e)
        => FinishPriorityDrag();

    private void PriorityListBox_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
    }

    private void ContinuePriorityDrag(ListBox listBox, PointerEventArgs e)
    {
        if (DataContext is not MainViewModel vm
            || string.IsNullOrWhiteSpace(_draggedPriorityName)
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        ReorderMoveAnimation.Follow(listBox, _draggedPriorityName, e);

        for (var step = 0; step < listBox.ItemCount; step++)
        {
            var targetPriority = ReorderMoveAnimation.GetDisplacementTarget(listBox, _draggedPriorityName) as string;
            if (string.IsNullOrWhiteSpace(targetPriority) || targetPriority == _draggedPriorityName)
            {
                break;
            }

            // During the drag, only update the visual order (no settings application). The
            // expensive settings application is deferred until the drag ends (FinishPriorityDrag).
            if (_draggedPriorityCollection == "cloud")
            {
                vm.ReorderCloudPriority(_draggedPriorityName, targetPriority, applySettingsImmediately: false);
            }
            else
            {
                vm.ReorderLocalPriority(_draggedPriorityName, targetPriority, applySettingsImmediately: false);
            }
        }

        e.Handled = true;
    }

    private void FinishPriorityDrag()
    {
        DisarmListDragHandlers();
        if (_draggedPriorityList is not null)
        {
            ReorderMoveAnimation.EndFollow(_draggedPriorityList, _draggedPriorityName);
        }

        // Apply the settings once when the drag ends. During the drag, only the visual order
        // was updated (no settings application), so this is the single expensive call.
        if (DataContext is MainViewModel vm && !string.IsNullOrWhiteSpace(_draggedPriorityCollection))
        {
            if (_draggedPriorityCollection == "cloud")
            {
                vm.ApplyCloudPriorityOrderToDetailedSettingsForDrag();
            }
            else
            {
                vm.ApplyLocalPriorityOrderToDetailedSettingsForDrag();
            }
        }

        _draggedPriorityName = null;
        _draggedPriorityCollection = null;
        _draggedPriorityList = null;
    }

    private void ArmListDragHandlers()
    {
        if (_listDragHandlersArmed)
        {
            return;
        }

        _listDragHandlersArmed = true;
        AddHandler(PointerMovedEvent, OnArmedListDragMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnArmedListDragReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void DisarmListDragHandlers()
    {
        if (!_listDragHandlersArmed)
        {
            return;
        }

        _listDragHandlersArmed = false;
        RemoveHandler(PointerMovedEvent, OnArmedListDragMoved);
        RemoveHandler(PointerReleasedEvent, OnArmedListDragReleased);
    }

    private void OnArmedListDragMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedPriorityList is not null && !string.IsNullOrWhiteSpace(_draggedPriorityName))
        {
            ContinuePriorityDrag(_draggedPriorityList, e);
            return;
        }

        if (_draggedQuickSelectSlot is not null)
        {
            ContinueQuickSelectDrag(e);
        }
    }

    private void OnArmedListDragReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_draggedPriorityName is not null)
        {
            FinishPriorityDrag();
        }
        else if (_draggedQuickSelectSlot is not null)
        {
            FinishQuickSelectSlotDrag();
        }

        e.Pointer.Capture(null);
    }

    private static ListBox? FindPriorityListBox(object? sender)
        => sender as ListBox ?? (sender as Control)?.FindAncestorOfType<ListBox>();

    private void OnHelpChromeLoaded(object? sender, RoutedEventArgs e)
    {
        ReloadHelpFromFile(force: true);
        StartHelpFileWatchers();

        // Help colours are read from the theme as the page is built, so a light or dark
        // switch has to draw the page again.
        ActualThemeVariantChanged -= OnHelpThemeVariantChanged;
        ActualThemeVariantChanged += OnHelpThemeVariantChanged;
    }

    private void OnHelpThemeVariantChanged(object? sender, EventArgs e)
        => ReloadHelpFromFile(force: true);

    private void OnMainTabsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // TabControl raises SelectionChanged while XAML is still loading, before this
        // window has a name scope. FindControl throws then and the app never appears.
        if (!IsInitialized)
        {
            return;
        }

        if (IsHelpTabSelected(sender as TabControl))
        {
            ReloadHelpFromFile(force: false);
        }

        if (IsSettingsTabSelected(sender as TabControl))
        {
            if (DataContext is MainViewModel vm)
            {
                _ = vm.LoadGpuVramFlyoutCommand.ExecuteAsync(null);
            }
        }

        // Update the window's minimum width based on the selected tab. Different tabs
        // have different minimum width requirements to prevent compression or word wrap.
        UpdateMinWidthForSelectedTab(sender as TabControl);
    }

    private bool IsSettingsTabSelected(TabControl? tabs = null)
    {
        tabs ??= this.FindControl<TabControl>("MainTabs");
        return tabs?.SelectedItem is TabItem item
            && Convert.ToString(item.Header) == "Settings";
    }

    private void UpdateMinWidthForSelectedTab(TabControl? tabs)
    {
        if (tabs?.SelectedItem is not TabItem item)
        {
            System.Diagnostics.Debug.WriteLine("[MinWidth] No selected tab found");
            return;
        }

        var header = Convert.ToString(item.Header);
        double baseMinWidth = header switch
        {
            "Quick Select" => 700.0,
            "Servers" => 800.0,
            "Model Profiles" => 750.0,
            "Live-Switch Testing" => 700.0,
            "Environment" => 700.0,
            "Settings" => 700.0,
            "Help" => 600.0,
            _ => 640.0
        };

        // The "UI enforce clean width" setting limits how narrow the window can be shrunk,
        // to protect the visibility of controls on all tabs. At Normal UI scale this is 1024px;
        // smaller UI scales reduce it proportionally (via the scale factor applied below).
        const double enforceCleanWidthBase = 1024.0;

        // Use the maximum of the tab's minimum width and the enforce-clean-width base.
        var effectiveMinWidth = Math.Max(baseMinWidth, enforceCleanWidthBase);

        // Apply the UI scale factor to the base minimum width.
        var mainVm = DataContext as MainViewModel;
        var scale = mainVm?.UiScaleFactor ?? 1.0;
        var newMinWidth = effectiveMinWidth * scale;
        
        // Only enforce the minimum width if "UI enforce clean width" is checked.
        if (mainVm != null && mainVm.EnforceCleanWidth)
        {
            System.Diagnostics.Debug.WriteLine($"[MinWidth] Tab={header}, base={baseMinWidth}, enforce={enforceCleanWidthBase}, scale={scale}, newMinWidth={newMinWidth}, currentMinWidth={MinWidth}");
            MinWidth = newMinWidth;
            System.Diagnostics.Debug.WriteLine($"[MinWidth] Set MinWidth to {MinWidth}");
        }
        else
        {
            // If "UI enforce clean width" is unchecked, use a smaller minimum width to allow
            // the window to shrink much smaller, at the cost of controls wrapping or compressing.
            var wrapMinWidth = 500.0 * scale;
            System.Diagnostics.Debug.WriteLine($"[MinWidth] Wrap mode: Tab={header}, scale={scale}, wrapMinWidth={wrapMinWidth}");
            MinWidth = wrapMinWidth;
        }
    }

    private bool IsHelpTabSelected(TabControl? tabs = null)
    {
        tabs ??= TryFindNamed<TabControl>("MainTabs");
        return tabs?.SelectedItem is TabItem item
            && string.Equals(Convert.ToString(item.Header), "Help", StringComparison.Ordinal);
    }

    private T? TryFindNamed<T>(string name) where T : Control
    {
        try
        {
            return this.FindControl<T>(name);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void ReloadHelpFromFile(bool force)
    {
        var panel = TryFindNamed<StackPanel>("HelpBodyPanel");
        if (panel is null)
        {
            return;
        }

        var path = HelpHtmlFile.FindNewest();
        if (path is null)
        {
            _loadedHelpPath = null;
            _loadedHelpUtc = DateTime.MinValue;
            HelpHtmlViewBuilder.ShowMessage(
                panel,
                "Help.html was not found next to AI-FluxMux or in the folder you started it from. Save Help.html there (Web Page, Filtered), then return to this tab.");
            RebuildHelpIndex();
            ApplyHelpFilter();
            return;
        }

        DateTime stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex)
        {
            HelpHtmlViewBuilder.ShowMessage(panel, "Could not read Help.html: " + ex.Message);
            RebuildHelpIndex();
            ApplyHelpFilter();
            return;
        }

        if (!force
            && _loadedHelpPath is not null
            && path.Equals(_loadedHelpPath, StringComparison.OrdinalIgnoreCase)
            && stamp == _loadedHelpUtc)
        {
            return;
        }

        var selected = this.FindControl<ListBox>("HelpIndexList")?.SelectedItem as HelpTopicEntry;
        var selectedTitle = selected?.Title;
        var selectedId = selected?.Id;
        HelpDocument document;
        try
        {
            document = LoadHelpDocument(path);
        }
        catch (Exception ex)
        {
            HelpHtmlViewBuilder.ShowMessage(panel, "Could not read Help.html: " + ex.Message);
            _loadedHelpPath = path;
            _loadedHelpUtc = stamp;
            RebuildHelpIndex();
            ApplyHelpFilter();
            return;
        }

        HelpHtmlViewBuilder.Populate(panel, document, OpenHelpLink);
        HelpUiNotes.Publish(document);
        _loadedHelpPath = path;
        _loadedHelpUtc = stamp;
        RebuildHelpIndex();
        ApplyHelpFilter();
        RestoreHelpSelection(selectedTitle, selectedId);
        StartHelpFileWatchers();
    }

    private static HelpDocument LoadHelpDocument(string path)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                return HelpHtmlParser.Load(path);
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(80);
            }
        }

        return HelpHtmlParser.Load(path);
    }

    private void OpenHelpLink(string url)
    {
        // A cross-reference inside Help.html jumps to that topic rather than handing an
        // anchor to the browser, which would leave the tab and land nowhere.
        if (url.StartsWith('#') && TryGoToHelpTopic(url[1..]))
        {
            return;
        }

        if (DataContext is MainViewModel vm)
        {
            vm.OpenExternalLinkCommand.Execute(url);
        }
    }

    /// <summary>
    /// Opens the Help tab and scrolls to a topic. Prefer a <c>topic.*</c> id so a
    /// Word or feed update can rename the Heading 2 title.
    /// </summary>
    public void ShowHelpTopic(string? reference)
    {
        var tabs = TryFindNamed<TabControl>("MainTabs");
        if (tabs is not null)
        {
            foreach (var item in tabs.Items)
            {
                if (item is TabItem tab
                    && string.Equals(Convert.ToString(tab.Header), "Help", StringComparison.Ordinal))
                {
                    tabs.SelectedItem = tab;
                    break;
                }
            }
        }

        ReloadHelpFromFile(force: false);
        if (!string.IsNullOrWhiteSpace(reference))
        {
            TryGoToHelpTopic(reference);
        }
    }

    /// <summary>
    /// Follows a link written in Help.html as <c>href="#Topic title"</c> or
    /// <c>href="#topic.port_rules"</c>.
    /// </summary>
    private bool TryGoToHelpTopic(string reference)
    {
        string wanted;
        try
        {
            wanted = Uri.UnescapeDataString(reference).Trim();
        }
        catch (UriFormatException)
        {
            wanted = reference.Trim();
        }

        if (wanted.Length == 0)
        {
            return false;
        }

        var match = HelpTopicSearch.FindByReference(_helpTopics, wanted);
        if (match?.Target is null)
        {
            return false;
        }

        // The body is never filtered, so the topic can be scrolled to even when a search
        // has taken it out of the index.
        var list = this.FindControl<ListBox>("HelpIndexList");
        if (list is not null && list.Items.Contains(match))
        {
            _helpIndexUpdating = true;
            try
            {
                list.SelectedItem = match;
            }
            finally
            {
                _helpIndexUpdating = false;
            }
        }

        ScrollHelpTopicIntoView(match.Target);
        return true;
    }

    private void RestoreHelpSelection(string? title, string? id = null)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var list = this.FindControl<ListBox>("HelpIndexList");
        if (list is null)
        {
            return;
        }

        var match = !string.IsNullOrWhiteSpace(id)
            ? _helpTopics.FirstOrDefault(topic =>
                topic.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            : null;
        match ??= !string.IsNullOrWhiteSpace(title)
            ? _helpTopics.FirstOrDefault(topic =>
                topic.Title.Equals(title, StringComparison.Ordinal))
            : null;
        if (match is null)
        {
            return;
        }

        _helpIndexUpdating = true;
        try
        {
            list.SelectedItem = match;
        }
        finally
        {
            _helpIndexUpdating = false;
        }
    }

    private void StartHelpFileWatchers()
    {
        var directories = HelpHtmlFile.CandidatePaths()
            .Select(Path.GetDirectoryName)
            .Where(dir => !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            .Select(dir => Path.GetFullPath(dir!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var watching = new HashSet<string>(
            _helpWatchers.Select(watcher => watcher.Path),
            StringComparer.OrdinalIgnoreCase);
        if (watching.SetEquals(directories))
        {
            return;
        }

        StopHelpFileWatchers();
        foreach (var dir in directories)
        {
            FileSystemWatcher watcher;
            try
            {
                watcher = new FileSystemWatcher(dir)
                {
                    NotifyFilter = NotifyFilters.FileName
                        | NotifyFilters.LastWrite
                        | NotifyFilters.Size
                        | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };
            }
            catch (Exception)
            {
                continue;
            }

            watcher.Changed += OnHelpFileDiskEvent;
            watcher.Created += OnHelpFileDiskEvent;
            watcher.Deleted += OnHelpFileDiskEvent;
            watcher.Renamed += OnHelpFileDiskEvent;
            _helpWatchers.Add(watcher);
        }
    }

    private void OnHelpFileDiskEvent(object sender, FileSystemEventArgs e)
    {
        if (!HelpHtmlFile.IsHelpFileName(e.Name) && !HelpHtmlFile.IsHelpFileName(e.FullPath))
        {
            return;
        }

        _helpReloadTimer ??= new Timer(_ =>
        {
            Dispatcher.UIThread.Post(() => ReloadHelpFromFile(force: true));
        });
        _helpReloadTimer.Change(500, Timeout.Infinite);
    }

    private void StopHelpFileWatchers()
    {
        _helpReloadTimer?.Dispose();
        _helpReloadTimer = null;
        foreach (var watcher in _helpWatchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnHelpFileDiskEvent;
            watcher.Created -= OnHelpFileDiskEvent;
            watcher.Deleted -= OnHelpFileDiskEvent;
            watcher.Renamed -= OnHelpFileDiskEvent;
            watcher.Dispose();
        }

        _helpWatchers.Clear();
    }

    private void OnHelpSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        ApplyHelpFilter();
    }

    private void OnHelpSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        var list = this.FindControl<ListBox>("HelpIndexList");
        if (list is null || list.ItemCount == 0)
        {
            return;
        }

        // Index 0 can be a section banner, which is a label rather than a topic.
        var first = list.ItemsSource?
            .OfType<HelpTopicEntry>()
            .FirstOrDefault(entry => entry.IsTopic);
        if (first is null)
        {
            return;
        }

        list.SelectedItem = first;
        ScrollHelpTopicIntoView(first.Target);
        e.Handled = true;
    }

    private void OnHelpIndexSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_helpIndexUpdating)
        {
            return;
        }

        var list = this.FindControl<ListBox>("HelpIndexList");
        if (list?.SelectedItem is HelpTopicEntry { IsTopic: true } entry)
        {
            ScrollHelpTopicIntoView(entry.Target);
        }
    }

    private void RebuildHelpIndex()
    {
        _helpTopics.Clear();
        var panel = this.FindControl<StackPanel>("HelpBodyPanel");
        if (panel is null)
        {
            return;
        }

        // Children are in document order, so a Heading 1 banner names every topic
        // that follows it until the next banner.
        var section = string.Empty;
        foreach (var child in panel.Children)
        {
            if (child is TextBlock banner && banner.Classes.Contains("helpSection"))
            {
                section = banner.Text ?? string.Empty;
                continue;
            }

            if (child is not StackPanel topic || !topic.Classes.Contains("helpTopic"))
            {
                continue;
            }

            var title = string.Empty;
            var id = string.Empty;
            if (topic.Tag is HelpTopicAnchor anchor)
            {
                title = anchor.Title;
                id = anchor.Id;
            }
            else
            {
                title = topic.Tag as string ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var body = new StringBuilder();
            if (id.Length > 0)
            {
                body.Append(id).Append(' ');
            }

            CollectHelpText(topic, body);
            _helpTopics.Add(new HelpTopicEntry
            {
                Title = title,
                Id = id,
                Target = topic,
                SearchText = body.ToString(),
                Section = section
            });
        }
    }

    private void ApplyHelpFilter()
    {
        var list = this.FindControl<ListBox>("HelpIndexList");
        var status = this.FindControl<TextBlock>("HelpSearchStatus");
        var search = this.FindControl<TextBox>("HelpSearchBox");
        if (list is null)
        {
            return;
        }

        var query = search?.Text ?? string.Empty;
        var matches = _helpTopics
            .Where(topic => HelpTopicSearch.Matches(topic.SearchText, query))
            .ToList();
        var display = HelpTopicSearch.GroupBySection(matches);

        _helpIndexUpdating = true;
        try
        {
            var keep = list.SelectedItem as HelpTopicEntry;
            list.ItemsSource = display;
            list.SelectedItem = keep is not null && display.Contains(keep) ? keep : null;
        }
        finally
        {
            _helpIndexUpdating = false;
        }

        if (status is null)
        {
            return;
        }

        if (matches.Count == 0)
        {
            status.Text = "No matching topics.";
            return;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            status.Text = matches.Count == 1 ? "1 topic." : matches.Count + " topics.";
            return;
        }

        status.Text = matches.Count == 1 ? "1 matching topic." : matches.Count + " matching topics.";
    }

    private void ScrollHelpTopicIntoView(Control? target)
    {
        if (target is null)
        {
            return;
        }

        void Scroll()
        {
            var scroll = this.FindControl<ScrollViewer>("HelpBodyScroll");
            if (scroll is null)
            {
                return;
            }

            var point = target.TranslatePoint(new Point(0, 0), scroll);
            if (point is { } offset)
            {
                scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y + offset.Y - 12));
            }
        }

        // The Help tab may not be fully laid out when this is called (e.g. when switching from
        // another tab). Post the scroll at multiple priorities and with a small async delay to
        // ensure the layout is complete before the offset is computed.
        Dispatcher.UIThread.Post(Scroll, DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(Scroll, DispatcherPriority.Background);
        _ = DelayThenScrollAsync(Scroll);
    }

    private async Task DelayThenScrollAsync(Action scrollAction)
    {
        await Task.Delay(80);
        if (Dispatcher.UIThread.CheckAccess())
        {
            scrollAction();
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(scrollAction);
        }
    }

    private static void CollectHelpText(Control node, StringBuilder text)
    {
        if (node is TextBlock textBlock)
        {
            if (!string.IsNullOrWhiteSpace(textBlock.Text))
            {
                Append(text, textBlock.Text);
            }
            else if (textBlock.Inlines is { Count: > 0 } inlines)
            {
                // A paragraph carrying a control name, a path, or a link is built from
                // inlines and leaves Text empty, so most of Help would not be searchable.
                foreach (var inline in inlines)
                {
                    if (inline is Run run)
                    {
                        Append(text, run.Text);
                    }
                }
            }
        }
        else if (node is Button button && button.Content is string content)
        {
            Append(text, content);
        }

        if (node is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is Control control)
                {
                    CollectHelpText(control, text);
                }
            }

            return;
        }

        if (node is Decorator decorator && decorator.Child is Control decorated)
        {
            CollectHelpText(decorated, text);
        }
    }

    /// <summary>
    /// Drops the spacing characters the Help view adds around runs, so a search for a
    /// path or a control name is not broken by them.
    /// </summary>
    private static void Append(StringBuilder text, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        foreach (var ch in value)
        {
            if (ch is not ('\u200B' or '\u2009'))
            {
                text.Append(ch);
            }
        }

        text.Append(' ');
    }
}