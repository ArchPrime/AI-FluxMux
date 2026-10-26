using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace FluxMux.Avalonia.ViewModels;

/// <summary>
/// Converts a bool value to expand/collapse text.
/// True = "Collapse", False = "Expand".
/// Used for toggle buttons that expand/collapse sections.
/// </summary>
public sealed class BoolToExpandTextConverter : IValueConverter
{
    public static readonly BoolToExpandTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            return b ? "Collapse" : "Expand";
        }
        return "Expand";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}