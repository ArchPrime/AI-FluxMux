using System;
using Avalonia.Data.Converters;
using FluxMux.Avalonia.Services.HailMary;

namespace FluxMux.Avalonia.ViewModels;

/// <summary>
/// Two-way converter between the Hail Mary Cline compaction strategy (enum) and a RadioButton's
/// IsChecked (bool), keyed by the strategy name in ConverterParameter. Used by the Hail Mary
/// picker when a Cline-like client is active (Stage 0 = WaitForCline, Stage 1 = ExternalRescue).
/// </summary>
public sealed class HailMaryStrategyToBoolConverter : IValueConverter
{
    public static readonly HailMaryStrategyToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is HailMaryStrategy strategy)
        {
            var name = strategy.ToString();
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
                "ExternalRescue" => HailMaryStrategy.ExternalRescue,
                _ => HailMaryStrategy.WaitForCline,
            };
        }

        // Unchecked: return the current value unchanged so the other radio buttons'
        // ConvertBack (which fire with false) do not clobber the selected strategy.
        return value is HailMaryStrategy existing ? existing : HailMaryStrategy.WaitForCline;
    }
}