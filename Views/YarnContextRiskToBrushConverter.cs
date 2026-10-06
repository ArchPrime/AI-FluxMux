using System;
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

    private static readonly IBrush SafeBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
    private static readonly IBrush ExceedsAvailableBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0x00, 0x00));

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is YarnContextRiskLevel riskLevel)
        {
            return riskLevel switch
            {
                YarnContextRiskLevel.ExceedsAvailable => ExceedsAvailableBrush,
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