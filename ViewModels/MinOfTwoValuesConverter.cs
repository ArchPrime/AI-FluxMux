using System;
using System.Collections.Generic;
using Avalonia.Data.Converters;

namespace FluxMux.Avalonia.ViewModels;

/// <summary>
/// A multi-value converter that returns the minimum of two numeric values.
/// Used to constrain the progress bar's width to the minimum of the graph pane's width
/// and the available space before the stress test controls.
/// </summary>
public sealed class MinOfTwoValuesConverter : IMultiValueConverter
{
    public static readonly MinOfTwoValuesConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (values.Count >= 2)
        {
            if (values[0] is double graphPaneWidth && values[1] is double availableSpace)
            {
                return Math.Min(graphPaneWidth, availableSpace);
            }
        }

        // Fallback: return the first value if it's not a valid array of two doubles.
        return values.Count > 0 ? values[0] : null;
    }

    public object?[] ConvertBack(object? value, Type[] targetTypes, object? parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}