using System;
using Avalonia.Data.Converters;
using FluxMux.Avalonia.Services.HailMary;

namespace FluxMux.Avalonia.ViewModels;

/// <summary>
/// Two-way converter between the Hail Mary Cline compaction strategy (enum) and a RadioButton's
/// IsChecked (bool), keyed by the strategy name in ConverterParameter. Used by the Hail Mary
/// picker (TrimInAiFluxMux for any Client app; WaitForCline and ExternalRescue for Cline).
/// </summary>
public sealed class HailMaryStrategyToBoolConverter : IValueConverter
{
    public static readonly HailMaryStrategyToBoolConverter Instance = new();

    // The last strategy value that was actually selected (via ConvertBack(true)). Both radio
    // buttons in the group are two-way bound to the SAME HailMarySelectedStrategy property, so
    // when one is checked the other is unchecked and fires ConvertBack(false, ...). If the
    // unchecked case returned a hardcoded default, the unchecked radio would clobber the
    // property with that default no matter which option the user picked. By remembering the
    // last selected value and returning it on unchecked, only the checked radio's
    // ConvertBack(true) actually mutates the property; the unchecked one is a no-op.
    private HailMaryStrategy _lastSelected = HailMaryStrategy.TrimInAiFluxMux;

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
            var selected = name switch
            {
                "ExternalRescue" => HailMaryStrategy.ExternalRescue,
                "WaitForCline" => HailMaryStrategy.WaitForCline,
                _ => HailMaryStrategy.TrimInAiFluxMux,
            };
            _lastSelected = selected;
            return selected;
        }

        // Unchecked: this radio was just deselected because the other radio in the group was
        // selected. Return the last-selected value unchanged so this unchecked event does NOT
        // overwrite the property the user just set.
        return _lastSelected;
    }
}