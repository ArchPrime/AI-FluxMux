using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace FluxMux.Updates.Core;

public static class FluxMuxAppInfo
{
    public const string Version = "0.3.2 beta";
    public const string ProductTitle = "AI-FluxMux v0.3.2 beta";
    public const string UserAgent = "AI-FluxMux/0.3.2-beta";
    public const string Copyright = "© 2026 Paul King · Architecture Prime Ltd";
}

public static class AppVersionComparer
{
    /// <summary>
    /// Compares two version strings. The comparison is semver-aware:
    /// 1. Compare the numeric major.minor.patch triple (missing parts = 0).
    /// 2. If the triple is equal, compare the prerelease suffix:
    ///    - No prerelease (stable) > any prerelease.
    ///    - Prerelease identifiers are compared in order: alpha < beta < rc < (none).
    ///    - Within the same identifier type, compare the numeric suffix (e.g. rc.2 > rc.1).
    ///    - If the identifier type and numeric suffix are equal, compare the full suffix lexicographically.
    /// 3. Non-prerelease suffixes (e.g. "beta" in "0.3.1 beta") are ignored for comparison
    ///    (treated as equal to no suffix) to preserve backward compatibility with the
    ///    existing "0.2 beta" == "0.2" test expectation.
    /// </summary>
    public static int Compare(string? current, string? published)
    {
        var left = Parse(current);
        var right = Parse(published);
        var length = Math.Max(left.Numeric.Length, right.Numeric.Length);
        for (var i = 0; i < length; i++)
        {
            var a = i < left.Numeric.Length ? left.Numeric[i] : 0;
            var b = i < right.Numeric.Length ? right.Numeric[i] : 0;
            if (a != b)
            {
                return a.CompareTo(b);
            }
        }

        // Numeric triple is equal; compare the prerelease suffix.
        return ComparePrerelease(left.Prerelease, right.Prerelease);
    }

    private static (int[] Numeric, string? Prerelease) Parse(string? version)
    {
        var text = (version ?? string.Empty).Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return ([0], null);
        }

        // Split off the prerelease suffix (everything after the first '-').
        string? prerelease = null;
        var dashIndex = text.IndexOf('-');
        if (dashIndex >= 0)
        {
            prerelease = text[(dashIndex + 1)..].Trim();
            text = text[..dashIndex].Trim();
        }

        var numeric = text.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part =>
            {
                var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
                return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : 0;
            })
            .DefaultIfEmpty(0)
            .ToArray();

        return (numeric, string.IsNullOrWhiteSpace(prerelease) ? null : prerelease);
    }

    /// <summary>
    /// Compares two prerelease suffixes. Returns negative if left < right, zero if equal, positive if left > right.
    /// A null prerelease (stable) is greater than any non-null prerelease.
    /// </summary>
    private static int ComparePrerelease(string? left, string? right)
    {
        if (left is null && right is null) return 0;
        if (left is null) return 1;  // stable > prerelease
        if (right is null) return -1; // prerelease < stable

        // Extract the identifier type (alpha, beta, rc, etc.) and the numeric suffix.
        var (leftType, leftNum) = ParsePrereleaseIdentifier(left);
        var (rightType, rightNum) = ParsePrereleaseIdentifier(right);

        // Compare the identifier type rank: alpha < beta < rc < (unknown/other).
        var leftRank = PrereleaseRank(leftType);
        var rightRank = PrereleaseRank(rightType);
        if (leftRank != rightRank)
        {
            return leftRank.CompareTo(rightRank);
        }

        // Same identifier type; compare the numeric suffix.
        if (leftNum != rightNum)
        {
            return leftNum.CompareTo(rightNum);
        }

        // Same type and number; fall back to lexicographic comparison of the full suffix.
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses a prerelease suffix into its identifier type and numeric component.
    /// Examples: "alpha.5" -> ("alpha", 5), "rc.2" -> ("rc", 2), "beta" -> ("beta", 0).
    /// </summary>
    private static (string Type, int Number) ParsePrereleaseIdentifier(string prerelease)
    {
        // Match the leading alphabetic identifier and an optional numeric suffix.
        var match = Regex.Match(prerelease, @"^([A-Za-z]+)(?:\.?(\d+))?", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return (prerelease, 0);
        }

        var type = match.Groups[1].Value.ToLowerInvariant();
        var number = int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : 0;
        return (type, number);
    }

    /// <summary>
    /// Returns a rank for a prerelease identifier type. Lower rank = earlier in the release cycle.
    /// alpha < beta < rc < (unknown/other).
    /// </summary>
    private static int PrereleaseRank(string type) => type switch
    {
        "alpha" => 0,
        "beta" => 1,
        "rc" => 2,
        _ => 3,
    };
}
