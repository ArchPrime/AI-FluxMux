using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace FluxMux.Avalonia.ViewModels;

/// <summary>
/// Converts a string value to a bool by checking if it's empty or null.
/// Empty or null strings are converted to false; non-empty strings are converted to true.
/// Used for IsVisible bindings that depend on a string property (e.g., bar value labels).
/// </summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public static readonly StringToVisibilityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s)
        {
            return !string.IsNullOrEmpty(s);
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}