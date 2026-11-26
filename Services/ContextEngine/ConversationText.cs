using System;
using System.Text.RegularExpressions;

namespace FluxMux.Avalonia.Services.ContextEngine;

/// <summary>
/// Separates what a person actually wrote from what a Client app wraps around it.
///
/// Client apps send their own text in the "user" role: Cline sends tool results
/// ("[read_file for 'x'] Result: ..."), environment dumps (&lt;environment_details&gt;),
/// resume and summarise requests ("[TASK RESUMPTION] ...", "Summarize this session ..."), and
/// wraps real input in tags (&lt;task&gt;, &lt;feedback&gt;, &lt;answer&gt;, &lt;user_message&gt;).
/// VIBE_STATE used every "user" message as if a person had typed it, so its Task became Cline's
/// "[TASK RESUMPTION] Please continue where you left off." and its intent-shift list filled with
/// tool output. Plain clients (Harness, any OpenAI-compatible chat) send ordinary text, which
/// passes through unchanged.
/// </summary>
public static class ConversationText
{
    private static readonly Regex EnvironmentDetails = new(
        @"<environment_details>.*?</environment_details>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Cline 4.x wraps typed input as <user_input mode="act"> (or "plan"), with an attribute, so the
    // tag may carry one; missing it put "user input mode act" into brief file names and summaries.
    private static readonly Regex WrappedHumanText = new(
        @"<(task|feedback|answer|user_message|user_input)(?:\s[^>]*)?>(.*?)</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "[read_file for 'Services/x.cs'] Result:" / "[execute_command for 'dotnet build'] Result:"
    private static readonly Regex ToolResultHeader = new(
        @"^\[[^\]\r\n]{1,300}\]\s*Result:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] ControlPrefixes =
    [
        "[TASK RESUMPTION]",
        "[ERROR]",
        "Summarize this session",
        "Summarize the conversation",
        "Summarise this session",
        "This session is being continued",
        "<tool_result",
        "<explicit_instructions",
        // AI-FluxMux's own inserted text: the compaction summary, the essence summary written
        // into Cline's compaction file, and the notes added to a tool result. None is a person's
        // words, and reading the summary as one made a compacted chat look like a new chat.
        "Compressed prior conversation context",
        "Context summary:",
        "Note from AI-FluxMux",
        "[AI-FluxMux:",
    ];

    /// <summary>
    /// True for an assistant-role message that AI-FluxMux wrote itself — the pause notice sent
    /// back when the operator clicks "Let me steer". The Client app keeps it in the chat as if the
    /// model had said it, so the session state was read from it: VIBE_STATE's State became
    /// "To break a mill, raise Temperature…" and was handed back to the model every turn.
    /// </summary>
    public static bool IsAiFluxMuxNotice(string? text)
    {
        var trimmed = (text ?? string.Empty).TrimStart();
        return trimmed.StartsWith(PortRulesPostMortem.SteerNoticeLead.Trim(), StringComparison.Ordinal)
            || trimmed.StartsWith(PortRulesPostMortem.ChatTurnCannotContinue.Trim(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The human-written part of a user-role message, or an empty string when the message is
    /// entirely Client-app control text or tool output.
    /// </summary>
    public static string HumanPart(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = EnvironmentDetails.Replace(text, string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var wrapped = WrappedHumanText.Match(trimmed);
        if (wrapped.Success)
        {
            return wrapped.Groups[2].Value.Trim();
        }

        foreach (var prefix in ControlPrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }
        }

        return ToolResultHeader.IsMatch(trimmed) ? string.Empty : trimmed;
    }

    /// <summary>Lower-cased, whitespace-collapsed form used to compare two texts for "same".</summary>
    public static string Normalize(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : Regex.Replace(text.Trim(), @"\s+", " ").ToLowerInvariant();
}
