using System;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Client-app stop strings are usually passed through. A bad list can
/// short-loop or cut think. Port rules may drop them.
/// </summary>
public static class LocalStopHygiene
{
    public static void Apply(JsonObject payload, string? mode)
    {
        if (payload is null)
        {
            return;
        }

        if (PortForwardingRules.NormalizeStopHygieneMode(mode) != PortForwardingRules.StopIgnore)
        {
            return;
        }

        payload.Remove("stop");
    }
}
