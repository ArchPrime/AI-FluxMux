using System;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// User-facing labels for cloud provider ids. Stored config, secrets, and
/// routing keep the short ids (for example Gemini).
/// </summary>
public static class CloudProviderDisplayNames
{
    public static string ToDisplayName(string? provider)
    {
        var id = (provider ?? string.Empty).Trim();
        if (id.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
        {
            return "Google/Gemini";
        }

        return id;
    }
}
