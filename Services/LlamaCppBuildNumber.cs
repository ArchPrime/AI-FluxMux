using System.Text.RegularExpressions;

namespace FluxMux.Avalonia.Services;

public static class LlamaCppBuildNumber
{
    private static readonly Regex BuildRegex = new(
        @"(?:build\s+|b)(\d{4,})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static int? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = BuildRegex.Match(text);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var build))
        {
            return null;
        }

        return build;
    }
}
