using System;
using System.Text;

namespace FluxMux.Avalonia.Services.ContextEngine;

/// <summary>
/// Renders a <see cref="VibeState"/> to the human-readable <c>VIBE_STATE.md</c> format and parses
/// it back. The file is a fenced <c>```json</c> block (the machine-readable source of truth)
/// followed by a short rendered summary (the human-readable view). Keeping the JSON block is what
/// makes the file round-trippable: the engine always re-reads the JSON, never the prose.
///
/// This is pure string manipulation — no I/O — so it is trivially testable.
/// </summary>
public static class VibeStateMarkdown
{
    private const string JsonFenceOpen = "```json";
    private const string JsonFenceClose = "```";

    /// <summary>
    /// Renders the full <c>VIBE_STATE.md</c> text for <paramref name="state"/>.
    /// </summary>
    public static string Render(VibeState state)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# VIBE STATE");
        sb.AppendLine();
        sb.AppendLine("_Auto-maintained by AI-FluxMux. Edit the JSON block below; the summary is regenerated on save._");
        sb.AppendLine();
        sb.AppendLine(JsonFenceOpen);
        sb.AppendLine(state.ToJson());
        sb.AppendLine(JsonFenceClose);
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine($"- **Task:** {OrDash(state.Task)}");
        sb.AppendLine($"- **State:** {OrDash(state.State)}");
        sb.AppendLine($"- **Next:** {OrDash(state.Next)}");
        sb.AppendLine($"- **Files:** {OrDash(state.Files)}");
        if (state.UpdatedUtc is not null)
        {
            sb.AppendLine($"- **Updated (UTC):** {state.UpdatedUtc.Value:yyyy-MM-dd HH:mm:ss}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds the short block to prepend to the system prompt on every local turn. It is kept
    /// deliberately terse (a few lines) so it costs almost nothing in tokens while still telling
    /// the model exactly where the session stands.
    /// </summary>
    public static string BuildReinjectionBlock(VibeState state)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[VIBE STATE — current session context]");
        sb.AppendLine($"Task: {OrDash(state.Task)}");
        sb.AppendLine($"State: {OrDash(state.State)}");
        // Next came from the model's reply and is no longer filled (27 Nov); an old note may
        // still hold one, which is not handed back as an instruction.
        if (!string.IsNullOrWhiteSpace(state.Files))
        {
            sb.AppendLine($"Key files: {state.Files}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Extracts the JSON block from a <c>VIBE_STATE.md</c> string. Returns <c>null</c> when no
    /// fenced JSON block is present.
    /// </summary>
    public static string? ExtractJsonBlock(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        var openIndex = markdown.IndexOf(JsonFenceOpen, StringComparison.Ordinal);
        if (openIndex < 0)
        {
            return null;
        }

        var jsonStart = openIndex + JsonFenceOpen.Length;
        var closeIndex = markdown.IndexOf(JsonFenceClose, jsonStart, StringComparison.Ordinal);
        if (closeIndex < 0)
        {
            return null;
        }

        return markdown[jsonStart..closeIndex].Trim();
    }

    private static string OrDash(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "—" : value;
    }
}