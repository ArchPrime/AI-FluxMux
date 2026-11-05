using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using System;
using System.Windows.Input;

namespace FluxMux.Avalonia.Controls;

/// <summary>
/// A two-segment pill that shows which GPU an app is on. The active segment is filled
/// (accent background + bold text); the inactive segment is muted. When <see cref="IsSwitchable"/>
/// is true the user can click the inactive segment to flip the app (raises <see cref="ToggleRequested"/>);
/// when false the whole pill is greyed and acts as a static badge (no interaction).
/// </summary>
public partial class GpuPillToggle : UserControl
{
    /// <summary>Which GPU is currently active. True = Motherboard GPU (iGPU), false = Discrete GPU.</summary>
    public static readonly StyledProperty<bool> IsOnMotherboardProperty =
        AvaloniaProperty.Register<GpuPillToggle, bool>(nameof(IsOnMotherboard));

    /// <summary>Whether the user may flip the GPU. False renders a static, greyed badge.</summary>
    public static readonly StyledProperty<bool> IsSwitchableProperty =
        AvaloniaProperty.Register<GpuPillToggle, bool>(nameof(IsSwitchable), true);

    /// <summary>Command invoked when the user clicks the pill (only when switchable).</summary>
    public static readonly StyledProperty<ICommand> ToggleCommandProperty =
        AvaloniaProperty.Register<GpuPillToggle, ICommand>(nameof(ToggleCommand));

    /// <summary>
    /// True when a GPU change is pending (registry set but app not yet restarted).
    /// Shows a pulsing border and a "pending" indicator so the user knows the app
    /// will switch GPUs after restart.
    /// </summary>
    public static readonly StyledProperty<bool> HasPendingChangeProperty =
        AvaloniaProperty.Register<GpuPillToggle, bool>(nameof(HasPendingChange));

    public GpuPillToggle()
    {
        InitializeComponent();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsOnMotherboardProperty || e.Property == IsSwitchableProperty ||
                e.Property == HasPendingChangeProperty)
            {
                ApplyState();
            }
        };
        Loaded += (_, _) => ApplyState();
    }

    public bool IsOnMotherboard
    {
        get => GetValue(IsOnMotherboardProperty);
        set => SetValue(IsOnMotherboardProperty, value);
    }

    public bool IsSwitchable
    {
        get => GetValue(IsSwitchableProperty);
        set => SetValue(IsSwitchableProperty, value);
    }

    public ICommand ToggleCommand
    {
        get => GetValue(ToggleCommandProperty);
        set => SetValue(ToggleCommandProperty, value);
    }

    public bool HasPendingChange
    {
        get => GetValue(HasPendingChangeProperty);
        set => SetValue(HasPendingChangeProperty, value);
    }

    private void ApplyState()
    {
        // Look up the named controls by name rather than relying on the x:Name-generated
        // fields, which can still be null if this runs before the XAML tree is fully
        // populated (e.g. a property change raised during construction). A premature call
        // is a no-op instead of a NullReferenceException.
        if (this.FindControl<Border>("RootBorder") is not Border rootBorder ||
            this.FindControl<Border>("DiscreteSeg") is not Border discreteSeg ||
            this.FindControl<Border>("MotherboardSeg") is not Border motherboardSeg ||
            this.FindControl<TextBlock>("DiscreteText") is not TextBlock discreteText ||
            this.FindControl<TextBlock>("MotherboardText") is not TextBlock motherboardText)
        {
            return;
        }

        var switchable = IsSwitchable;
        var onMotherboard = IsOnMotherboard;
        var pending = HasPendingChange;

        // Resolve brushes from the active theme.
        var variant = Application.Current?.ActualThemeVariant ?? ThemeVariant.Light;
        IBrush? Resolve(string key) =>
            Application.Current?.TryFindResource(key, variant, out var v) == true && v is IBrush b ? b : null;

        var accent = Resolve("FluxListItemSelectedBackground") ?? Brushes.Transparent;
        var heading = Resolve("FluxHeadingForeground") ?? Brushes.Black;
        var secondary = Resolve("FluxSecondaryForeground") ?? Brushes.DimGray;

        // Active segment gets the accent fill + bold text; inactive is transparent + muted.
        var activeBg = accent;
        var activeFg = heading;
        var inactiveBg = Brushes.Transparent;
        var inactiveFg = secondary;

        if (onMotherboard)
        {
            motherboardSeg.Background = activeBg;
            motherboardText.Foreground = activeFg;
            motherboardText.FontWeight = FontWeight.SemiBold;
            discreteSeg.Background = inactiveBg;
            discreteText.Foreground = inactiveFg;
            discreteText.FontWeight = FontWeight.Normal;
        }
        else
        {
            discreteSeg.Background = activeBg;
            discreteText.Foreground = activeFg;
            discreteText.FontWeight = FontWeight.SemiBold;
            motherboardSeg.Background = inactiveBg;
            motherboardText.Foreground = inactiveFg;
            motherboardText.FontWeight = FontWeight.Normal;
        }

        // Grey the whole pill when not switchable (static badge).
        rootBorder.Opacity = switchable ? 1.0 : 0.5;
        rootBorder.IsHitTestVisible = switchable;

        // Pending change indicator: the active segment's background pulses to draw attention.
        // No border is added — the toggle itself pulses.
        if (pending && switchable)
        {
            // Keep the normal border (no special pending border).
            rootBorder.BorderBrush = Resolve("FluxMutedBorder") ?? Brushes.Gray;
            rootBorder.BorderThickness = new Thickness(1);
            // Start pulsing the active segment's background.
            StartPulseAnimation(onMotherboard ? motherboardSeg : discreteSeg);
        }
        else
        {
            rootBorder.BorderBrush = Resolve("FluxMutedBorder") ?? Brushes.Gray;
            rootBorder.BorderThickness = new Thickness(1);
            StopPulseAnimation();
        }
    }

    private void StartPulseAnimation(Border activeSeg)
    {
        if (_pulseTimer is not null) return; // already pulsing
        _pulseActiveSeg = activeSeg;

        _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _pulseTimer.Tick += (_, _) =>
        {
            _pulsePhase = !_pulsePhase;
            // Pulse the active segment's background opacity (not the whole pill).
            if (_pulseActiveSeg is not null)
            {
                _pulseActiveSeg.Opacity = _pulsePhase ? 1.0 : 0.5;
            }
        };
        _pulseTimer.Start();
    }

    private void StopPulseAnimation()
    {
        if (_pulseTimer is not null)
        {
            _pulseTimer.Stop();
            _pulseTimer.Tick -= null;
            _pulseTimer = null;
        }
        // Reset the active segment's opacity.
        if (_pulseActiveSeg is not null)
        {
            _pulseActiveSeg.Opacity = 1.0;
        }
        _pulseActiveSeg = null;
        _pulsePhase = false;
    }

    private DispatcherTimer? _pulseTimer;
    private bool _pulsePhase;
    private Border? _pulseActiveSeg;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsSwitchable)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed)
        {
            var cmd = ToggleCommand;
            if (cmd?.CanExecute(DataContext) ?? false)
            {
                cmd.Execute(DataContext);
            }
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
