using System;
using Avalonia.Data.Converters;

namespace FluxMux.Avalonia.ViewModels;

/// <summary>
/// Inverts a bool (true &lt;-&gt; false). Used by the Hail Mary picker to show/hide the
/// strategy/prune view when a rescue compaction is pending (and vice versa).
/// </summary>
public sealed class InvertedBoolConverter : IValueConverter
{
    public static readonly InvertedBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        return value is bool b ? !b : value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        return value is bool b ? !b : value;
    }
}