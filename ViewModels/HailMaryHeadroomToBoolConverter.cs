using System;
using Avalonia.Data.Converters;
using FluxMux.Avalonia.Services.HailMary;

namespace FluxMux.Avalonia.ViewModels;

/// <summary>
/// Two-way converter between the Hail Mary headroom preference (enum) and a RadioButton's
/// IsChecked (bool), keyed by the headroom name in ConverterParameter. Used by the Hail Mary
/// picker's "make headroom" control for the Cline rescue path.
///
/// IMPORTANT: This converter operates on the Services.HailMary.HailMaryHeadroom enum — the SAME
/// type the MainViewModel.HailMaryHeadroom property is declared as. (An earlier version defined a
/// duplicate HailMaryHeadroom enum in this ViewModels namespace, which caused the binding to fail
/// silently: the converter's ConvertBack returned the ViewModels enum, but the property expected
/// the Services enum, so the type mismatch made the binding no-op and the property stayed at its
/// default (Balanced/40%) no matter which option the user picked — the "always 40%" bug.)
/// </summary>
public sealed class HailMaryHeadroomToBoolConverter : IValueConverter
{
    public static readonly HailMaryHeadroomToBoolConverter Instance = new();

    // The last headroom value that was actually selected (via ConvertBack(true)). All three
    // radio buttons in the group are two-way bound to the SAME HailMaryHeadroom property, so
    // when one is checked the other two are unchecked and each fires ConvertBack(false, ...).
    // If the unchecked case returned a hardcoded default, the last unchecked radio would
    // clobber the property with that default no matter which option the user picked — making
    // "PreserveMore (20%)" and "LeaveMoreRoom (60%)" silently collapse to the default (40%).
    // By remembering the last selected value and returning it on unchecked, only the checked
    // radio's ConvertBack(true) actually mutates the property; the unchecked ones are no-ops.
    private HailMaryHeadroom _lastSelected = HailMaryHeadroom.Balanced;

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is HailMaryHeadroom headroom)
        {
            var name = headroom.ToString();
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
                "PreserveMore" => HailMaryHeadroom.PreserveMore,
                "LeaveMoreRoom" => HailMaryHeadroom.LeaveMoreRoom,
                _ => HailMaryHeadroom.Balanced,
            };
            _lastSelected = selected;
            return selected;
        }

        // Unchecked: this radio was just deselected because another radio in the group was
        // selected. Return the last-selected value unchanged so this unchecked event does NOT
        // overwrite the property the user just set. (Returning a hardcoded default here is what
        // caused the "always 40%" bug — the last unchecked radio would clobber the selection.)
        return _lastSelected;
    }
}