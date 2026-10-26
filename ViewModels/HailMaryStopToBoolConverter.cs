using System;
using Avalonia.Data.Converters;
using FluxMux.Avalonia.Services;

namespace FluxMux.Avalonia.ViewModels;

/// <summary>
/// Two-way converter between the Hail Mary aggression stop (enum) and a RadioButton's
/// IsChecked (bool), keyed by the stop name in ConverterParameter. Used by the one-off
/// Hail Mary picker on both the banner and the Diagnostics bar.
/// </summary>
public sealed class HailMaryStopToBoolConverter : IValueConverter
{
    public static readonly HailMaryStopToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is HailMaryCompaction.HailMaryAggression stop)
        {
            var name = stop.ToString();
            return string.Equals(name, parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is true)
        {
            var name = parameter?.ToString() ?? string.Empty;
            return name switch
            {
                "Gentle" => HailMaryCompaction.HailMaryAggression.Gentle,
                "Aggressive" => HailMaryCompaction.HailMaryAggression.Aggressive,
                _ => HailMaryCompaction.HailMaryAggression.Balanced,
            };
        }

        // Unchecked: return the current value unchanged so the other radio buttons'
        // ConvertBack (which fire with false) do not clobber the selected stop.
        return value is HailMaryCompaction.HailMaryAggression existing ? existing : HailMaryCompaction.HailMaryAggression.Balanced;
    }
}

