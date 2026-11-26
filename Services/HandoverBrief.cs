using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluxMux.Avalonia.Services.ContextEngine;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// "Chat handover brief" (26 Nov): a text the person pastes as the first message
/// of a new chat — after a chat is stuck, before switching Client app or model, or to park a task
/// for later. It replaced Hail Mary's "Trimming it in AI-FluxMux", which cut only the one request it
/// forwarded: the Client app resent the whole chat on the next turn, so it could not reset a chat,
/// and the automatic compaction and 95 % last resort already did the rest of its job.
///
/// Built from the chat exactly as the Client app last sent it, with the same pieces compaction keeps
/// (so it shares that code and its tests): the opening request word for word, the summary of the
/// older part (commands run, values found in tool output, the person's earlier messages, decisions),
/// the files changed, the latest turns word for word, and the current task. It is not built from
/// VIBE_STATE: that note is captured a line at a time and can hold a stale task or a copy of the last
/// reply. File contents are left out on purpose — the files are on disk and the new chat rereads
/// them — and so is AI-FluxMux's own inserted text. Pure, so it is unit-tested against the
/// Live-Switch test's ten planted facts.
/// </summary>
public static class HandoverBrief
{
    /// <summary>Messages at the end of the chat that are quoted rather than summarised.</summary>
    public const int RecentMessages = 12;

    public const int OpeningChars = 4000;

    /// <summary>
    /// Hail Mary does not rebuild a chat whose newest message alone is over this share of the
    /// Context: "retry" would send it again and overflow again.
    /// </summary>
    public const double OversizedLastMessageShare = 0.25;

    public const int RecentMessageChars = 1500;
    public const int RecentToolResultChars = 800;
    public const int RecentTotalChars = 10000;
    public const int MaxFilesChanged = 40;

    // A brief is read once by a fresh chat, so it can list more of the person's messages than the
    // summary compaction repeats on every turn.
    public const int MaxPersonMessages = 60;
    public const int MaxSummaryChars = 20000;

    public const string Title = "# Hand over brief";

    private static readonly Regex EditToolName = new(
        "(write|edit|replace|create|apply|patch|insert|delete|rename|move)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A call that returns a file's contents, as DescribeToolCall names it ("read_file: src/a.py",
    // "run_command: cat src/a.py", "execute_command: Get-Content a.txt").
    private static readonly Regex FileReadCall = new(
        @"^(\w*(read|view|open)\w*\b|\w+:\s*(cat|type|head|tail|more|less|Get-Content|gc)\s)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] PathFields = ["path", "file_path", "file", "target_file", "filename"];

    /// <summary>
    /// The brief, or null when the chat holds nothing the person wrote. <paramref name="clientApp"/>
    /// names where it came from (null = not recognised); <paramref name="capturedUtc"/> is when the
    /// Client app sent it. The person's final message has a section of its own and is either in
    /// full or left out (<paramref name="includeLastMessage"/>), never cut: a cut copy helps
    /// neither the new chat nor the person, who may want to edit and resend it (26 Nov). Whether to
    /// leave it out is the caller's (see <see cref="FinalMessageImpact"/>).
    /// </summary>
    public static string? Build(
        JsonArray? messages,
        string? clientApp,
        DateTime capturedUtc,
        bool includeLastMessage = true)
    {
        if (messages is null || messages.Count == 0)
        {
            return null;
        }

        var first = LocalHistoryCompaction.IndexOfFirstRealUser(messages);
        if (first < 0)
        {
            return null;
        }

        var last = LocalHistoryCompaction.IndexOfLastRealUser(messages);
        var opening = HumanText(messages[first] as JsonObject);
        if (string.IsNullOrWhiteSpace(opening))
        {
            return null;
        }

        // Everything after the opening and before the quoted tail is summarised. A short chat is
        // quoted whole.
        var keepStart = messages.Count - RecentMessages <= first + 1
            ? first + 1
            : LocalHistoryCompaction.AlignKeepStart(messages, messages.Count - RecentMessages);

        var text = new StringBuilder();
        text.Append(Title).Append(" — ")
            .Append(capturedUtc.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture))
            .Append(", from a ").Append(string.IsNullOrWhiteSpace(clientApp) ? "Client app" : clientApp).Append(" chat\n\n")
            .Append("This brief continues an earlier chat. AI-FluxMux wrote it from that chat: the opening request "
                    + "and the latest turns word for word, the older part summarised. File contents are not in it; "
                    + "the files are on disk, so reread any you need before changing them.\n");

        text.Append("\n## Original request\n").Append(Cap(opening, OpeningChars)).Append('\n');

        if (keepStart > first + 1)
        {
            var summary = LocalHistoryCompaction.BuildForwardSummary(messages, keepStart, MaxPersonMessages, MaxSummaryChars);
            var body = summary?.Split('\n', 2) is [_, var rest] ? rest.Trim() : string.Empty;
            if (body.Length > 0)
            {
                text.Append("\n## Earlier in the chat (summarised)\n").Append(body).Append('\n');
            }

            var earlierImages = messages.Take(keepStart).OfType<JsonObject>().Sum(ImageCount);
            if (earlierImages > 0)
            {
                text.Append("(" + earlierImages + (earlierImages == 1 ? " image was" : " images were")
                            + " attached earlier in the chat; a text brief cannot carry them, so attach any that still matter.)\n");
            }
        }

        var files = FilesChanged(messages);
        if (files.Count > 0)
        {
            text.Append("\n## Files changed\n");
            foreach (var file in files)
            {
                text.Append("- ").Append(file).Append('\n');
            }
        }

        var lastIndex = last > first ? last : -1;
        var recent = RecentLines(messages, keepStart, lastIndex);
        if (recent.Count > 0)
        {
            text.Append("\n## Latest turns (word for word)\n");
            foreach (var line in recent)
            {
                text.Append(line).Append('\n');
            }
        }

        var current = lastIndex >= 0 ? HumanText(messages[lastIndex] as JsonObject) : string.Empty;
        if (!string.IsNullOrWhiteSpace(current))
        {
            var answered = messages.Skip(lastIndex + 1).OfType<JsonObject>().Any(m => LocalHistoryCompaction.RoleOf(m) == "assistant");
            text.Append(answered
                ? "\n## Your final message\n"
                : "\n## Your final message (not answered yet)\n");
            if (!includeLastMessage)
            {
                text.Append("Left out of this brief on purpose (" + current.Length.ToString("N0", CultureInfo.InvariantCulture)
                            + " characters): it may be what overflowed the earlier chat. Ask for it again, in a different form or in smaller parts, if it is still needed.\n");
            }
            else
            {
                text.Append(current).Append('\n').Append(ImageNote(messages[lastIndex] as JsonObject));
            }
        }

        text.Append("\n## Continue\n")
            .Append("Carry on from where the latest turns stop. Treat the values and decisions above as settled "
                    + "unless the files on disk say otherwise, and do not redo work already listed as done.\n");
        return text.ToString();
    }

    /// <summary>
    /// The summary Hail Mary writes in place of messages [0, <paramref name="keepStart"/>) when it
    /// rebuilds a stuck chat in the Client app's own files (26 Nov). Same pieces as the brief, but
    /// no quoted tail and no "latest message" section: the messages from <paramref name="keepStart"/>
    /// on stay in the chat word for word. Capped at <paramref name="maxChars"/>; the older-part
    /// summary gives way first, the opening request last. Null when the chat holds nothing the
    /// person wrote.
    /// </summary>
    public static string? BuildRescueSummary(JsonArray? messages, int keepStart, int maxChars)
    {
        if (messages is null || messages.Count == 0)
        {
            return null;
        }

        var first = LocalHistoryCompaction.IndexOfFirstRealUser(messages);
        var opening = first >= 0 ? HumanText(messages[first] as JsonObject) : string.Empty;
        if (string.IsNullOrWhiteSpace(opening))
        {
            return null;
        }

        var head = new StringBuilder();
        head.Append("Summary of the earlier part of this chat, written by AI-FluxMux (Hail Mary) to make room. "
                    + "The most recent messages follow word for word. File contents are not in it; reread a file before changing it.\n");
        head.Append("\n## Original request\n").Append(Cap(opening, Math.Min(OpeningChars, Math.Max(200, maxChars / 3)))).Append('\n');

        var files = FilesChanged(messages);
        var tail = new StringBuilder();
        if (files.Count > 0)
        {
            tail.Append("\n## Files changed\n");
            foreach (var file in files)
            {
                tail.Append("- ").Append(file).Append('\n');
            }
        }

        var room = maxChars - head.Length - tail.Length;
        var middle = string.Empty;
        if (keepStart > first + 1 && room > 200)
        {
            var summary = LocalHistoryCompaction.BuildForwardSummary(messages, keepStart, MaxPersonMessages, Math.Min(MaxSummaryChars, room - 40));
            var body = summary?.Split('\n', 2) is [_, var rest] ? rest.Trim() : string.Empty;
            if (body.Length > 0)
            {
                middle = "\n## Earlier in the chat (summarised)\n" + body + "\n";
            }
        }

        var text = head + middle + tail.ToString();
        return text.Length <= maxChars ? text : text[..maxChars];
    }

    /// <summary>"2026-11-26 1025 tidy the billing service and get.md": the date sorts, the words say which task.</summary>
    public static string FileName(JsonArray messages, DateTime capturedUtc)
    {
        var first = LocalHistoryCompaction.IndexOfFirstRealUser(messages);
        var opening = first >= 0 ? HumanText(messages[first] as JsonObject) : string.Empty;
        var words = Regex.Replace(opening, @"[^\p{L}\p{N} ]+", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !w.Equals("task", StringComparison.OrdinalIgnoreCase))
            .Take(7);
        var label = string.Join(" ", words).ToLowerInvariant();
        return capturedUtc.ToLocalTime().ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)
               + (label.Length > 0 ? " " + (label.Length > 60 ? label[..60].TrimEnd() : label) : string.Empty) + ".md";
    }

    /// <summary>The person's last message in the chat (their own words), or empty when it is the opening request.</summary>
    public static string LastPersonMessage(JsonArray? messages)
    {
        if (messages is null)
        {
            return string.Empty;
        }

        var first = LocalHistoryCompaction.IndexOfFirstRealUser(messages);
        var last = LocalHistoryCompaction.IndexOfLastRealUser(messages);
        return last > first ? HumanText(messages[last] as JsonObject) : string.Empty;
    }

    /// <summary>
    /// What is known about the person's final message, in estimated tokens (chars / 4 scaled by the
    /// measured ratio, pictures counted as the gateway counts them): the chat before it, the message
    /// itself, everything after it (tool calls, results, replies), whether the chat was then
    /// refused as too large, and the room a new chat would have with this brief. What it cannot
    /// know is stated in <see cref="Describe"/>: if the chat filled before the work the message set
    /// off was done, or the message was refused before any reply, its full cost is unknown.
    /// </summary>
    public sealed record FinalMessageImpact(
        int ContextTokens,
        int BeforeTokens,
        int MessageTokens,
        int AfterTokens,
        int ToolCallsAfter,
        bool AnythingAfter,
        bool Overflowed,
        int NewChatTokens)
    {
        public int FreeTokens => Math.Max(0, ContextTokens - BeforeTokens - MessageTokens - AfterTokens);

        /// <summary>Left out by default only when the chat was refused for size after the final message.</summary>
        public bool ExcludeByDefault => Overflowed;

        private string Of(int tokens)
            => "about " + tokens.ToString("N0", CultureInfo.InvariantCulture) + "/" + ContextTokens.ToString("N0", CultureInfo.InvariantCulture)
               + " tokens (" + Percent(tokens) + " of the Context)";

        private string Percent(int tokens)
            => (ContextTokens > 0 ? (int)Math.Round(100.0 * tokens / ContextTokens) : 0).ToString(CultureInfo.InvariantCulture) + " %";

        private static string Choice(bool included)
            => included
                ? "Your final message is included; untick to leave it out."
                : "Tick to include your final message anyway.";

        /// <summary>The notice's sentence about the final message: the facts, and where they stop.</summary>
        public string Describe(bool included)
        {
            if (ContextTokens <= 0)
            {
                return Overflowed
                    ? "The chat was refused as too large after your final message; its size against the Context is not known. " + Choice(included)
                    : string.Empty;
            }

            if (Overflowed && !AnythingAfter)
            {
                return "Your final message required " + Of(MessageTokens) + ", but there were only "
                       + Math.Max(0, ContextTokens - BeforeTokens).ToString("N0", CultureInfo.InvariantCulture)
                       + " tokens free, so the chat failed. " + Choice(included);
            }

            if (Overflowed)
            {
                var remaining = Math.Max(0, ContextTokens - BeforeTokens - MessageTokens);
                return "Your final message occupied " + Of(MessageTokens) + ", and triggered a model response"
                       + (ToolCallsAfter > 0 ? " (" + ToolCallsAfter.ToString("N0", CultureInfo.InvariantCulture) + (ToolCallsAfter == 1 ? " tool call)" : " tool calls)") : string.Empty)
                       + " that used up all the remaining context (" + Percent(remaining) + ") before the chat failed. "
                       + "What the final cost of that reply would have been is unknown. " + Choice(included);
            }

            return "The model had free space of " + Of(FreeTokens) + " at time of handover brief preparation. "
                   + "A new chat under the same model should have around "
                   + Math.Max(0, ContextTokens - NewChatTokens).ToString("N0", CultureInfo.InvariantCulture) + "/" + ContextTokens.ToString("N0", CultureInfo.InvariantCulture)
                   + " tokens free (" + Percent(Math.Max(0, ContextTokens - NewChatTokens)) + " of the Context).";
        }
    }

    /// <summary>
    /// Measures <see cref="FinalMessageImpact"/> for the chat. <paramref name="briefChars"/> is the
    /// brief's length, which with the system prompt is what a new chat starts with.
    /// </summary>
    public static FinalMessageImpact? Impact(JsonArray? messages, int contextTokens, double tokenRatio, bool overflowed, int briefChars)
    {
        if (messages is null)
        {
            return null;
        }

        var first = LocalHistoryCompaction.IndexOfFirstRealUser(messages);
        var last = LocalHistoryCompaction.IndexOfLastRealUser(messages);
        if (last <= first)
        {
            return null;
        }

        var ratio = HailMary.RescuePlanner.NormaliseRatio(tokenRatio);
        int Tokens(IEnumerable<JsonNode?> part)
        {
            var array = new JsonArray();
            foreach (var node in part)
            {
                array.Add(node?.DeepClone());
            }

            return (int)Math.Ceiling(LocalRequestOverlayRouting.EstimatePromptTokens(new JsonObject { ["messages"] = array }) * ratio);
        }

        var after = messages.Skip(last + 1).Where(m => LocalHistoryCompaction.RoleOf(m as JsonObject ?? new JsonObject()) != "system").ToList();
        var system = messages.Where(m => LocalHistoryCompaction.RoleOf(m as JsonObject ?? new JsonObject()) == "system");
        return new FinalMessageImpact(
            contextTokens,
            Tokens(messages.Take(last)),
            Tokens([messages[last]]),
            Tokens(after),
            after.OfType<JsonObject>().Sum(m => (m["tool_calls"] as JsonArray)?.Count ?? 0),
            after.Count > 0,
            overflowed,
            Tokens(system) + (int)Math.Ceiling(briefChars / 4.0 * ratio));
    }

    /// <summary>Pictures in a message, which a text brief cannot carry.</summary>
    internal static int ImageCount(JsonObject? message)
        => message?["content"] is JsonArray parts
            ? parts.OfType<JsonObject>().Count(p => (p["type"]?.ToString() ?? string.Empty) is "image_url" or "image" or "input_image")
            : 0;

    private static string ImageNote(JsonObject? message)
        => ImageCount(message) is var n and > 0
            ? "(" + n + (n == 1 ? " image was" : " images were") + " attached here; " + (n == 1 ? "it is" : "they are") + " not in this brief, so attach " + (n == 1 ? "it" : "them") + " again if needed)\n"
            : string.Empty;

    private static string HumanText(JsonObject? message)
        => message is null
            ? string.Empty
            : ConversationText.HumanPart(LocalHistoryCompaction.ExtractText(message["content"])).Trim();

    private static string Cap(string text, int max)
        => text.Length <= max ? text : text[..max].TrimEnd() + " …";

    /// <summary>Paths named by file-changing tool calls, in the order first changed.</summary>
    internal static List<string> FilesChanged(JsonArray messages)
    {
        var files = new List<string>();
        foreach (var message in messages.OfType<JsonObject>())
        {
            if (message["tool_calls"] is not JsonArray calls)
            {
                continue;
            }

            foreach (var call in calls.OfType<JsonObject>())
            {
                var name = call["function"]?["name"]?.ToString() ?? call["name"]?.ToString() ?? string.Empty;
                if (!EditToolName.IsMatch(name))
                {
                    continue;
                }

                var path = ArgumentPath(call["function"]?["arguments"] ?? call["arguments"]);
                if (!string.IsNullOrWhiteSpace(path) && !files.Contains(path, StringComparer.OrdinalIgnoreCase) && files.Count < MaxFilesChanged)
                {
                    files.Add(path);
                }
            }
        }

        return files;
    }

    private static string? ArgumentPath(JsonNode? arguments)
    {
        var args = arguments as JsonObject;
        if (args is null && arguments is JsonValue raw && raw.TryGetValue<string>(out var json))
        {
            try
            {
                args = JsonNode.Parse(json) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        return args is null
            ? null
            : PathFields.Select(f => args[f]?.ToString()).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))?.Trim();
    }

    /// <summary>The quoted tail, oldest first, trimmed from the oldest end to fit the cap.</summary>
    private static List<string> RecentLines(JsonArray messages, int keepStart, int skipIndex = -1)
    {
        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = new List<string>();
        for (var i = keepStart; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject message)
            {
                continue;
            }

            // The person's last message has its own section, so it can be left out.
            var role = LocalHistoryCompaction.RoleOf(message);
            if (role == "system" || i == skipIndex)
            {
                continue;
            }

            if (LocalHistoryCompaction.IsToolFollowUp(message))
            {
                var result = LocalHistoryCompaction.ExtractText(message["content"]).Trim();
                var source = callNames.TryGetValue(message["tool_call_id"]?.ToString() ?? string.Empty, out var named) ? named : "tool";
                if (result.Length > 0)
                {
                    // A file's contents are left out even here: the new chat rereads the file, and a
                    // quoted copy may already be out of date. Command output (a test run, an error)
                    // is what the work found, so it is quoted.
                    lines.Add(FileReadCall.IsMatch(source)
                        ? "- result of " + source + ": (file contents left out; reread the file if needed)"
                        : "- result of " + source + ": " + Cap(result, RecentToolResultChars));
                }

                continue;
            }

            if (role == "user")
            {
                var said = HumanText(message);
                if (said.Length > 0 && !ConversationText.IsAiFluxMuxNotice(said))
                {
                    lines.Add("- person: " + Cap(said, RecentMessageChars) + (ImageNote(message) is { Length: > 0 } note ? " " + note.TrimEnd() : string.Empty));
                }

                continue;
            }

            var reply = LocalHistoryCompaction.ExtractText(message["content"]).Trim();
            if (reply.Length > 0 && !ConversationText.IsAiFluxMuxNotice(reply))
            {
                lines.Add("- assistant: " + Cap(reply, RecentMessageChars));
            }

            if (message["tool_calls"] is JsonArray calls)
            {
                foreach (var call in calls.OfType<JsonObject>())
                {
                    var described = LocalToolResultClearing.DescribeToolCall(call);
                    if (call["id"]?.ToString() is { Length: > 0 } id)
                    {
                        callNames[id] = described;
                    }

                    if (described.Length > 0)
                    {
                        lines.Add("- assistant ran " + described);
                    }
                }
            }
        }

        var total = lines.Sum(l => l.Length + 1);
        while (lines.Count > 1 && total > RecentTotalChars)
        {
            total -= lines[0].Length + 1;
            lines.RemoveAt(0);
        }

        return lines;
    }
}
