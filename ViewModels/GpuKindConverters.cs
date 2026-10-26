using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using FluxMux.Avalonia.Services;

namespace FluxMux.Avalonia.ViewModels;

/// <summary>
/// Converts a collection of VramProcessListItem to a filtered list based on GpuKind.
/// Used to segregate the process list into VRAM and RAM sections.
/// </summary>
public static class GpuKindConverters
{
    public static readonly GpuKindFilterConverter DiscreteVramFilter = new(GpuKind.DiscreteVram);
    public static readonly GpuKindFilterConverter MotherboardRamFilter = new(GpuKind.MotherboardRam);

    public sealed class GpuKindFilterConverter : IValueConverter
    {
        private readonly GpuKind _targetKind;

        public GpuKindFilterConverter(GpuKind targetKind)
        {
            _targetKind = targetKind;
        }

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is IEnumerable<VramProcessListItem> items)
            {
                var filtered = new List<VramProcessListItem>();
                foreach (var item in items)
                {
                    if (item.GpuKind == _targetKind)
                    {
                        filtered.Add(item);
                    }
                }
                return filtered;
            }
            return new List<VramProcessListItem>();
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}