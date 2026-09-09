using System;
using System.Globalization;
using System.Linq;

namespace FluxMux.Avalonia.Services;

public static class FluxMuxAppInfo
{
    public const string Version = "0.2 beta";
    public const string ProductTitle = "AI-FluxMux v0.2 beta";
    public const string UserAgent = "AI-FluxMux/0.2-beta";
}

public static class AppVersionComparer
{
    public static int Compare(string? current, string? published)
    {
        var left = Parse(current);
        var right = Parse(published);
        var length = Math.Max(left.Length, right.Length);
        for (var i = 0; i < length; i++)
        {
            var a = i < left.Length ? left[i] : 0;
            var b = i < right.Length ? right[i] : 0;
            if (a != b)
            {
                return a.CompareTo(b);
            }
        }

        return 0;
    }

    private static int[] Parse(string? version)
    {
        var text = (version ?? string.Empty).Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return [0];
        }

        return text.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part =>
            {
                var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
                return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : 0;
            })
            .DefaultIfEmpty(0)
            .ToArray();
    }
}
