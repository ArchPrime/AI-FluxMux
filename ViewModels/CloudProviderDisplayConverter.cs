using System;
using System.Globalization;
using Avalonia.Data.Converters;
using FluxMux.Avalonia.Services;

namespace FluxMux.Avalonia.ViewModels;

public sealed class CloudProviderDisplayConverter : IValueConverter
{
    public static CloudProviderDisplayConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        CloudProviderDisplayNames.ToDisplayName(value?.ToString());

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
