using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Names the Client app that sent a chat request. Hail Mary, the saved chats (one per Client app,
/// <see cref="SavedChats"/>) and the handover brief all act on the Client app whose chat it is, and
/// several can share Port at once (pi, Cline, Harness, OpenCode…). The Client app dropdown is only
/// setup notes: with it left on Cline, a stuck Harness chat was offered Cline's rescue (25 Nov).
///
/// Two kinds of tell, read from each app's own source on 26 Nov (NEXT_SESSION_PLAN.md lists them):
/// the request's <c>User-Agent</c> or <c>X-Title</c>, where the app sets its own, and the opening of
/// its system prompt. The header is checked first because it is steadier: a custom mode or prompt
/// file (pi's SYSTEM.md, Roo/Kilo modes, Continue's baseSystemMessage) can replace the opening line,
/// and prompts change between versions (pi's did). Apps that leave the User-Agent to their SDK send
/// the SDK's name ("OpenAI/JS", "ai-sdk"), which several share, so those are not used as tells.
/// Returns the app's name, or null when nothing is recognised, so callers fall back to the dropdown.
/// </summary>
public static class ClientAppDetector
{
    public const string Cline = "Cline";
    public const string Harness = "DeepSeek Harness";
    public const string RooCode = "Roo Code";
    public const string KiloCode = "Kilo Code";
    public const string OpenCode = "OpenCode";
    public const string Pi = "pi";
    public const string Aider = "Aider";
    public const string Continue = "Continue";
    public const string Goose = "Goose";
    public const string GitHubCopilot = "GitHub Copilot";

    // Header tells: the start of the User-Agent ("Cline/4.1.27", "RooCode/3.x", "Kilo-Code/…",
    // "opencode/latest/1.2/opencode") or the X-Title the app sends.
    private static readonly (string Prefix, string App)[] UserAgentTells =
    [
        ("Cline/", Cline),
        ("RooCode/", RooCode),
        ("Kilo-Code/", KiloCode),
        ("opencode/", OpenCode),
    ];

    private static readonly (string Title, string App)[] TitleTells =
    [
        ("Roo Code", RooCode),
        ("Kilo Code", KiloCode),
        ("opencode", OpenCode),
        ("Cline", Cline),
    ];

    // Prompt tells, in order: an app's own opening before a phrase another app might quote. Kilo
    // Code is built on OpenCode's core, so its marker is checked before OpenCode's.
    private static readonly (string Marker, StringComparison Comparison, string App)[] PromptTells =
    [
        ("powered by DeepSeek Harness", StringComparison.OrdinalIgnoreCase, Harness),
        ("You are Cline", StringComparison.Ordinal, Cline),
        ("You are Roo,", StringComparison.Ordinal, RooCode),
        ("You are Kilo,", StringComparison.Ordinal, KiloCode),
        ("running in OpenCode, a coding agent harness", StringComparison.Ordinal, OpenCode),
        ("inside pi, a coding agent harness", StringComparison.Ordinal, Pi),
        ("Act as an expert software developer", StringComparison.Ordinal, Aider),
        ("You are in agent mode", StringComparison.Ordinal, Continue),
        ("You are in chat mode", StringComparison.Ordinal, Continue),
        ("AI agent called goose", StringComparison.Ordinal, Goose),
        ("working with a user in the VS Code editor", StringComparison.Ordinal, GitHubCopilot),
    ];

    /// <summary>The Client app from the request headers, then its system prompt.</summary>
    public static string? FromRequest(JsonObject? payload, string? userAgent, string? title = null)
        => FromHeaders(userAgent, title) ?? FromPayload(payload);

    public static string? FromHeaders(string? userAgent, string? title)
    {
        var agent = (userAgent ?? string.Empty).Trim();
        foreach (var (prefix, app) in UserAgentTells)
        {
            if (agent.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return app;
            }
        }

        var titled = (title ?? string.Empty).Trim();
        foreach (var (name, app) in TitleTells)
        {
            if (titled.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return app;
            }
        }

        return null;
    }

    public static string? FromPayload(JsonObject? payload)
    {
        if (payload?["messages"] is not JsonArray messages)
        {
            return null;
        }

        var system = messages.OfType<JsonObject>()
            .FirstOrDefault(m => string.Equals(m["role"]?.ToString(), "system", StringComparison.OrdinalIgnoreCase));
        return system is null ? null : FromSystemPrompt(TextOf(system["content"]));
    }

    public static string? FromSystemPrompt(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        // Only the opening matters, and a system prompt can be tens of KB. Continue opens with an
        // <important_rules> block before its mode line, so a little more than the first line is read.
        var head = text.Length > 2000 ? text[..2000] : text;
        foreach (var (marker, comparison, app) in PromptTells)
        {
            if (head.Contains(marker, comparison))
            {
                return app;
            }
        }

        return null;
    }

    private static string TextOf(JsonNode? content) => content switch
    {
        JsonValue value => value.ToString(),
        JsonArray parts => string.Join("\n", parts.OfType<JsonObject>().Select(p => p["text"]?.ToString() ?? string.Empty)),
        _ => string.Empty,
    };
}
