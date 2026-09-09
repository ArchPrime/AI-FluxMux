using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace FluxMux.Avalonia.ViewModels;

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
