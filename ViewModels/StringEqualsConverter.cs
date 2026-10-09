using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace FluxMux.Avalonia.ViewModels;

/// <summary>
/// Converts a string value to a bool by comparing it to a parameter.
/// Used for IsVisible bindings that depend on a string property.
/// </summary>
public sealed class StringEqualsConverter : IValueConverter
{
    public static readonly StringEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string valueStr && parameter is string paramStr)
        {
            return valueStr.Equals(paramStr, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}