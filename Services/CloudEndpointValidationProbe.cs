using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Tiny chat used by Validate to endpoint for cloud profiles.
/// Must be large enough for Gemini thinking models, must not send temperature
/// (Gemini 3.6+ rejects or ignores it), and must force the cloud route so a
/// launched local model cannot steal the probe.
/// </summary>
public static class CloudEndpointValidationProbe
{
    public const string ForceRouteHeader = "X-FluxMux-Force-Route";
    public const string UserText = "Reply with the single word OK.";
    public const int MaxTokens = 1024;

    public static JsonObject CreatePayload(string model)
    {
        return new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(model) ? "local" : model.Trim(),
            ["stream"] = false,
            ["max_tokens"] = MaxTokens,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = UserText
                }
            }
        };
    }
}
