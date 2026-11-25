using System;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using FluxMux.Avalonia.ViewModels;

namespace FluxMux.Avalonia.Views;

/// <summary>
/// Converts a <see cref="YarnContextRiskLevel"/> to a <see cref="IBrush"/> for UI display.
/// Safe options use the default foreground; options that exceed available memory use red.
/// </summary>
public sealed class YarnContextRiskToBrushConverter : IValueConverter
{
    public static readonly YarnContextRiskToBrushConverter Instance = new();

    // Safe options use the theme's text colour: a fixed #333333 was near-invisible in Dark (26 Nov).
    private static readonly IBrush FallbackSafeBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
    private static readonly IBrush PoorPerformanceBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x00));
    private static readonly IBrush ExceedsAvailableBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0x00, 0x00));

    private static IBrush SafeBrush
        => global::Avalonia.Application.Current is { } app
           && app.TryFindResource("FluxHeadingForeground", app.ActualThemeVariant, out var brush)
           && brush is IBrush themed
            ? themed
            : FallbackSafeBrush;

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is YarnContextRiskLevel riskLevel)
        {
            return riskLevel switch
            {
                YarnContextRiskLevel.ExceedsAvailable => ExceedsAvailableBrush,
                YarnContextRiskLevel.PoorPerformance => PoorPerformanceBrush,
                _ => SafeBrush
            };
        }

        return SafeBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}