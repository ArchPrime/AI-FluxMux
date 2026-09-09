using System;

namespace FluxMux.Avalonia.Services;

public static class FluxMuxCloudCallLimits
{
    public static bool PromptAsksForCloud(string? userText)
    {
        var lowered = (userText ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lowered))
        {
            return false;
        }

        return lowered.Contains("use the cloud")
               || lowered.Contains("use gpt")
               || lowered.Contains("use claude")
               || lowered.Contains("use gemini")
               || lowered.Contains("use copilot");
    }

    public static bool StayOnLocalWhenLow(bool localReady, string? userText, bool toolContinuation)
        => localReady && (toolContinuation || !PromptAsksForCloud(userText));
}
