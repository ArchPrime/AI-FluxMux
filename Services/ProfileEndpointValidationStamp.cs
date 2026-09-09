using System;
using System.Globalization;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

public static class ProfileEndpointValidationStamp
{
    public const string ValidatedUtcKey = "EndpointValidatedUtc";
    public const string WarningKey = "EndpointWarning";

    public static void Apply(JsonObject profile, bool validated, string? warning)
    {
        if (validated)
        {
            profile[ValidatedUtcKey] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            profile.Remove(WarningKey);
            return;
        }

        if (string.IsNullOrWhiteSpace(warning))
        {
            return;
        }

        profile.Remove(ValidatedUtcKey);
        profile[WarningKey] = LocalHealthGuidance.FormatProfilePanelWarning(warning);
    }
}
