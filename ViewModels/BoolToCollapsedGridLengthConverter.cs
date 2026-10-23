using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace FluxMux.Avalonia.ViewModels;

/// <summary>
/// Converts a bool to its inverse: true → false, false → true.
/// Used to show a control only when a flag is false (e.g. the "Stress test" button is
/// hidden while a test is running).
///
/// NOTE: The return type is a raw bool, not Visibility. Avalonia's binding engine coerces
/// the bool to the target property's type (Visibility for IsVisible). This is the existing
/// behavior in the codebase — changing it to return Visibility directly caused compile
/// errors because the Visibility enum's namespace differs between Avalonia versions.
/// </summary>
public sealed class InvertBoolToVisibilityConverter : IValueConverter
{
    public static readonly InvertBoolToVisibilityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToCollapsedGridLengthConverter : IValueConverter
{
    public static readonly BoolToCollapsedGridLengthConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? GridLength.Auto : new GridLength(0);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
