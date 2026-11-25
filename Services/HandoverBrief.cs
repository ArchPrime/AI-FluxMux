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
/// "Create hand over brief for fresh chat" (26 Nov): a text the person pastes as the first message
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
    public const int CurrentTaskChars = 2000;
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
    /// Client app sent it.
    /// </summary>
    public static string? Build(JsonArray? messages, string? clientApp, DateTime capturedUtc)
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

        var recent = RecentLines(messages, keepStart);
        if (recent.Count > 0)
        {
            text.Append("\n## Latest turns (word for word)\n");
            foreach (var line in recent)
            {
                text.Append(line).Append('\n');
            }
        }

        var current = last > first ? HumanText(messages[last] as JsonObject) : string.Empty;
        if (!string.IsNullOrWhiteSpace(current))
        {
            text.Append("\n## Your latest message\n").Append(Cap(current, CurrentTaskChars)).Append('\n');
        }

        text.Append("\n## Continue\n")
            .Append("Carry on from where the latest turns stop. Treat the values and decisions above as settled "
                    + "unless the files on disk say otherwise, and do not redo work already listed as done.\n");
        return text.ToString();
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
    private static List<string> RecentLines(JsonArray messages, int keepStart)
    {
        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = new List<string>();
        for (var i = keepStart; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject message)
            {
                continue;
            }

            var role = LocalHistoryCompaction.RoleOf(message);
            if (role == "system")
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
                    lines.Add("- person: " + Cap(said, RecentMessageChars));
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
