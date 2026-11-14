using System;
using Avalonia.Data.Converters;

namespace FluxMux.Avalonia.ViewModels;

/// <summary>
/// The Hail Mary rescue "make headroom" preference. Controls how much free context the external
/// compaction leaves for resuming the chat. Higher headroom = smaller summary = less nuance
/// preserved but safer; lower headroom = bigger summary = more nuance but less safety margin.
/// </summary>
public enum HailMaryHeadroom
{
    /// <summary>Preserve more nuance; leave ~20% free context. Riskier if context is critically full.</summary>
    PreserveMore,

    /// <summary>Balanced (default); leave ~40% free context. The "just enough" default.</summary>
    Balanced,

    /// <summary>Leave more room; ~60% free context. Safest; drops nuance first.</summary>
    LeaveMoreRoom,
}

/// <summary>
/// Two-way converter between the Hail Mary headroom preference (enum) and a RadioButton's
/// IsChecked (bool), keyed by the headroom name in ConverterParameter. Used by the Hail Mary
/// picker's "make headroom" control for the Cline rescue path.
/// </summary>
public sealed class HailMaryHeadroomToBoolConverter : IValueConverter
{
    public static readonly HailMaryHeadroomToBoolConverter Instance = new();

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
            return name switch
            {
                "PreserveMore" => HailMaryHeadroom.PreserveMore,
                "LeaveMoreRoom" => HailMaryHeadroom.LeaveMoreRoom,
                _ => HailMaryHeadroom.Balanced,
            };
        }

        return value is HailMaryHeadroom existing ? existing : HailMaryHeadroom.Balanced;
    }
}