using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace FluxMux.Avalonia.Controls;

public partial class AnimatedAppLogo : UserControl
{
    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<AnimatedAppLogo, bool>(nameof(IsPlaying));

    private const int FrameCount = 8;
    private const double TurnSeconds = 4.0;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Bitmap?[] _frames = new Bitmap?[FrameCount];
    private DateTime _startedUtc;
    private bool _running;
    private int _shown = -1;
    private bool _framesLoaded;

    public bool IsPlaying
    {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    static AnimatedAppLogo()
    {
        IsPlayingProperty.Changed.AddClassHandler<AnimatedAppLogo>((logo, _) => logo.ApplyPlaying());
    }

    public AnimatedAppLogo()
    {
        InitializeComponent();
        _timer.Tick += OnTick;
        AttachedToVisualTree += (_, _) => ApplyPlaying();
        DetachedFromVisualTree += (_, _) => StopTimer(resetPose: true);
    }

    private void ApplyPlaying()
    {
        if (IsPlaying)
        {
            StartTimer();
        }
        else
        {
            StopTimer(resetPose: true);
        }
    }

    private void EnsureFrames()
    {
        if (_framesLoaded)
        {
            return;
        }

        _framesLoaded = true;
        for (var i = 0; i < FrameCount; i++)
        {
            var uri = new Uri($"avares://FluxMux.Avalonia/Assets/Logos/AI-FluxMux-logo-kf-{i + 1}.png");
            if (!AssetLoader.Exists(uri))
            {
                continue;
            }

            try
            {
                using var stream = AssetLoader.Open(uri);
                _frames[i] = new Bitmap(stream);
            }
            catch
            {
                _frames[i] = null;
            }
        }
    }

    private bool HasFrames()
    {
        EnsureFrames();
        for (var i = 0; i < FrameCount; i++)
        {
            if (_frames[i] is null)
            {
                return false;
            }
        }

        return true;
    }

    private void StartTimer()
    {
        if (_running)
        {
            return;
        }

        if (!HasFrames())
        {
            return;
        }

        _running = true;
        _startedUtc = DateTime.UtcNow;
        _shown = -1;
        LogoFull.IsVisible = false;
        LogoAnim.IsVisible = true;
        ShowFrame(0);
        _timer.Start();
    }

    private void StopTimer(bool resetPose)
    {
        _timer.Stop();
        _running = false;
        if (resetPose)
        {
            ResetPose();
        }
    }

    private void ResetPose()
    {
        LogoAnim.IsVisible = false;
        LogoAnim.Source = null;
        LogoFull.IsVisible = true;
        _shown = -1;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        try
        {
            var elapsed = (DateTime.UtcNow - _startedUtc).TotalSeconds;
            var idx = (int)(elapsed / TurnSeconds * FrameCount) % FrameCount;
            if (idx < 0)
            {
                idx = 0;
            }

            ShowFrame(idx);
        }
        catch
        {
            StopTimer(resetPose: false);
        }
    }

    private void ShowFrame(int idx)
    {
        if (idx == _shown)
        {
            return;
        }

        var frame = _frames[idx];
        if (frame is null)
        {
            return;
        }

        LogoAnim.Source = frame;
        _shown = idx;
    }
}
