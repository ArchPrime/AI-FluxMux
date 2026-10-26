using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Forward pruning: a per-turn fill throttle. On EVERY local turn, as it happens, it trims the
/// turn's own waste so the context fills more slowly. This is deliberately the OPPOSITE of
/// conventional compaction (<see cref="LocalHistoryCompaction"/>, which summarizes OLDER turns
/// into one block when the window is filling) and of Hail Mary (<see cref="HailMaryCompaction"/>,
/// the one-off emergency rear-prune). Forward pruning runs from the very first turn, is
/// conservative (minimal loss), and never touches the user's current task.
///
/// The conservative pipeline (in order, each independent):
///   1. dedupe identical messages (Cline retries, re-sent user text) — pure waste;
///   2. collapse repeated tool-call loops (a shell/read command re-issued) — stub the repeats;
///   3. truncate oversized older tool-result bodies to a token cap (head + tail + elision).
///
/// Image stripping is intentionally NOT part of forward pruning: it is lossier and stays in the
/// manual Hail Mary path.
///
/// The token budget uses the model's MEASURED prompt-token count when available (the
/// 4-chars/token estimate over-counts for dense tokenizers like Qwen, which would make the
/// throttle a no-op).
/// </summary>
public static class ForwardPrune
{
    /// <summary>
    /// Token cap for an older tool-result body. Head + tail are kept with an elision marker
    /// between. Reuses the gateway's 4-chars/token estimate (cap × 4 chars).
    /// </summary>
    public const int ToolResultTokenCap = 1000;

    /// <summary>
    /// How many of the most recent tool results keep their full body. Everything older is
    /// truncated to the cap.
    /// </summary>
    public const int KeepRecentResults = 8;

    /// <summary>
    /// When a tool-call key repeats at least this many times, the middle repeats are pruned
    /// (first and last kept). Exact repeats are always pruned regardless of count.
    /// </summary>
    public const int LoopMiddlePruneThreshold = 3;

    /// <summary>
    /// Runs the conservative forward-prune on the payload, mutating it in place. Safe to call on
    /// every turn: when there is nothing to prune it returns a no-op result quickly.
    /// <paramref name="measuredTokens"/> is the model's real prompt-token count when available
    /// (used for the "does it already fit" short-circuit); when null the 4-chars/token estimate
    /// is used.
    /// </summary>
    public static ForwardPruneResult PruneTurn(JsonObject payload, int contextTokens, int? measuredTokens = null)
    {
        if (payload is null || payload["messages"] is not JsonArray)
        {
            return ForwardPruneResult.None;
        }

        var before = measuredTokens ?? LocalRequestOverlayRouting.EstimatePromptTokens(payload);
        var target = contextTokens > 0 ? contextTokens : int.MaxValue;
        if (before <= target)
        {
            return ForwardPruneResult.None; // already under the window; nothing to throttle
        }

        var report = new ForwardPruneReport
        {
            BeforeTokens = before,
            TargetTokens = target,
        };

        var best = before;

        if (DedupeIdenticalMessages(payload, report))
        {
            best = Math.Min(best, ReportAfter(payload, report));
        }

        if (CollapseToolLoops(payload, report))
        {
            best = Math.Min(best, ReportAfter(payload, report));
        }

        if (TruncateOversizedToolResults(payload, report))
        {
            best = Math.Min(best, ReportAfter(payload, report));
        }

        report.AfterTokens = best;
        return report.ToResult();
    }

    private static int ReportAfter(JsonObject payload, ForwardPruneReport report)
    {
        var t = LocalRequestOverlayRouting.EstimatePromptTokens(payload);
        report.AfterTokens = t;
        return t;
    }

    /// <summary>
    /// Stage 1: drop exact-duplicate messages (role + full JSON content identical). The first
    /// occurrence is kept; later copies are removed. Never dedupes the system prompt.
    ///
    /// Performance note: this runs on EVERY turn. A naive implementation serializes every
    /// message to a full JSON string each time (message.ToJsonString()), which is a significant
    /// CPU cost on large tool-heavy payloads (megabytes of tool results / file reads serialized
    /// repeatedly per turn). To keep the per-turn throttle cheap, we fingerprint each message with
    /// a cheap hash of its role + content length + a hash of the content, and only fall back to a
    /// full ToJsonString() comparison when two messages share the same cheap fingerprint (a
    /// collision candidate). In the common case (no duplicates) this avoids any full serialization.
    /// </summary>
    private static bool DedupeIdenticalMessages(JsonObject payload, ForwardPruneReport report)
    {
        if (payload["messages"] is not JsonArray messages)
        {
            return false;
        }

        // fingerprint -> first index seen with that fingerprint.
        var byFingerprint = new Dictionary<string, int>(StringComparer.Ordinal);
        var removed = new List<int>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject message)
            {
                continue;
            }

            if (RoleOf(message) == "system")
            {
                continue;
            }

            var fingerprint = CheapFingerprint(message);
            if (!byFingerprint.TryGetValue(fingerprint, out var firstIndex))
            {
                byFingerprint[fingerprint] = i;
                continue;
            }

            // Collision candidate: confirm with a full JSON comparison so we only drop true
            // duplicates. This full serialization happens only when the cheap fingerprint matches,
            // which is rare (no duplicates in a normal chat).
            if (string.Equals(
                    message.ToJsonString() ?? string.Empty,
                    (messages[firstIndex] as JsonObject)?.ToJsonString() ?? string.Empty,
                    StringComparison.Ordinal))
            {
                removed.Add(i);
            }
            else
            {
                // Different content, same cheap fingerprint. Track the latest index so a later
                // true duplicate of THIS message is still caught.
                byFingerprint[fingerprint] = i;
            }
        }

        if (removed.Count == 0)
        {
            return false;
        }

        foreach (var index in removed.OrderByDescending(x => x))
        {
            messages.RemoveAt(index);
        }

        report.DedupedMessages = removed.Count;
        report.Stages.Add("dedupe: removed " + removed.Count + " duplicate message(s)");
        return true;
    }

    /// <summary>
    /// Stage 2: collapse repeated tool-call loops. One detector keyed on the tool name: shell
    /// tools key on the command string; read tools key on (path, range). For each distinct key,
    /// the first occurrence is kept. Exact repeats are always pruned; when a key repeats at
    /// least <see cref="LoopMiddlePruneThreshold"/> times, the middle repeats are pruned too
    /// (first and last kept). A pruned repeat's tool result is stubbed with an "already ran"
    /// line so the model knows the content exists earlier and does not loop into a fresh call.
    /// </summary>
    private static bool CollapseToolLoops(JsonObject payload, ForwardPruneReport report)
    {
        if (payload["messages"] is not JsonArray messages)
        {
            return false;
        }

        var calls = new List<(int MessageIndex, int CallOrdinal, string Key, string Describe)>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject assistant
                || RoleOf(assistant) != "assistant"
                || assistant["tool_calls"] is not JsonArray toolCalls)
            {
                continue;
            }

            var ordinal = 0;
            foreach (var node in toolCalls.OfType<JsonObject>())
            {
                var key = LoopKey(node);
                if (!string.IsNullOrEmpty(key))
                {
                    calls.Add((i, ordinal, key, LocalToolResultClearing.DescribeToolCall(node)));
                }

                ordinal++;
            }
        }

        if (calls.Count == 0)
        {
            return false;
        }

        var groups = new Dictionary<string, List<(int MessageIndex, int CallOrdinal, string Describe)>>();
        var order = new List<string>();
        foreach (var call in calls)
        {
            if (!groups.TryGetValue(call.Key, out var list))
            {
                list = new List<(int, int, string)>();
                groups[call.Key] = list;
                order.Add(call.Key);
            }

            list.Add((call.MessageIndex, call.CallOrdinal, call.Describe));
        }

        var prune = new HashSet<(int, int)>();
        foreach (var key in order)
        {
            var list = groups[key];
            if (list.Count < 2)
            {
                continue;
            }

            for (var n = 1; n < list.Count; n++)
            {
                prune.Add((list[n].MessageIndex, list[n].CallOrdinal));
            }

            if (list.Count >= LoopMiddlePruneThreshold)
            {
                prune.Remove((list[^1].MessageIndex, list[^1].CallOrdinal));
            }
        }

        if (prune.Count == 0)
        {
            return false;
        }

        var stubbed = 0;
        foreach (var (messageIndex, callOrdinal) in prune)
        {
            if (messages[messageIndex] is not JsonObject assistant
                || assistant["tool_calls"] is not JsonArray toolCalls
                || callOrdinal >= toolCalls.Count)
            {
                continue;
            }

            var call = toolCalls[callOrdinal] as JsonObject;
            var callId = call?["id"]?.ToString();
            var describe = LocalToolResultClearing.DescribeToolCall(call ?? new JsonObject());
            if (StubMatchingResult(messages, messageIndex, callId, describe))
            {
                stubbed++;
            }
        }

        if (stubbed == 0)
        {
            return false;
        }

        report.LoopsCollapsed = prune.Count;
        report.LoopResultsStubbed = stubbed;
        report.Stages.Add("loops: collapsed " + prune.Count + " repeated tool call(s), stubbed " + stubbed + " result(s)");
        return true;
    }


    /// <summary>
    /// Stage 3: truncate oversized older tool-result bodies to <see cref="ToolResultTokenCap"/>
    /// tokens (head + tail with an elision marker). The most recent <see cref="KeepRecentResults"/>
    /// tool results keep their full body.
    /// </summary>
    private static bool TruncateOversizedToolResults(JsonObject payload, ForwardPruneReport report)
    {
        if (payload["messages"] is not JsonArray messages)
        {
            return false;
        }

        var resultIndexes = new List<int>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is JsonObject msg && LocalToolResultClearing.IsToolResult(msg))
            {
                resultIndexes.Add(i);
            }
        }

        if (resultIndexes.Count <= KeepRecentResults)
        {
            return false;
        }

        var keepFrom = resultIndexes.Count - KeepRecentResults;
        var capChars = ToolResultTokenCap * 4; // 4-chars/token estimate.
        var truncated = 0;
        for (var n = 0; n < keepFrom; n++)
        {
            var index = resultIndexes[n];
            if (messages[index] is not JsonObject result)
            {
                continue;
            }

            var text = ExtractText(result["content"]);
            if (text.StartsWith(LocalToolResultClearing.OmittedMark, StringComparison.Ordinal))
            {
                continue; // already stubbed
            }

            if (text.Length <= capChars)
            {
                continue;
            }

            result["content"] = TruncateHeadTail(text, capChars);
            truncated++;
        }

        if (truncated == 0)
        {
            return false;
        }

        report.ResultsTruncated = truncated;
        report.Stages.Add("results: truncated " + truncated + " oversized tool result(s) to " + ToolResultTokenCap + " tokens");
        return true;
    }

    // ---- helpers (self-contained; mirror the Hail Mary stage helpers) ----

    private static string LoopKey(JsonObject call)
    {
        var name = LocalSessionArtifactPolicy.ReadToolName(call);
        var args = call["function"]?["arguments"] ?? call["arguments"];

        if (IsShellToolName(name))
        {
            var command = ReadArgField(args, "command", "cmd", "input");
            return string.IsNullOrWhiteSpace(command) ? string.Empty : "shell\u0001" + command.Trim();
        }

        if (IsReadToolName(name))
        {
            var path = LocalSessionArtifactPolicy.ReadToolPath(call);
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var offset = ReadArgField(args, "start_line", "offset", "start");
            var limit = ReadArgField(args, "end_line", "limit", "count", "max_lines");
            return "read\u0001" + path.Trim() + "\u0001" + offset.Trim() + "\u0001" + limit.Trim();
        }

        return string.Empty;
    }

    private static bool IsShellToolName(string name)
    {
        var value = (name ?? string.Empty).Trim();
        return value.Equals("execute_command", StringComparison.OrdinalIgnoreCase)
            || value.Equals("run_command", StringComparison.OrdinalIgnoreCase)
            || value.Equals("shell", StringComparison.OrdinalIgnoreCase)
            || value.Equals("bash", StringComparison.OrdinalIgnoreCase)
            || value.Equals("command", StringComparison.OrdinalIgnoreCase)
            || value.Equals("run_terminal_cmd", StringComparison.OrdinalIgnoreCase)
            || value.Equals("terminal", StringComparison.OrdinalIgnoreCase)
            || value.Equals("powershell", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReadToolName(string name)
    {
        var value = (name ?? string.Empty).Trim();
        return value.Equals("read_file", StringComparison.OrdinalIgnoreCase)
            || value.Equals("read_files", StringComparison.OrdinalIgnoreCase)
            || value.Equals("read", StringComparison.OrdinalIgnoreCase)
            || value.Equals("view", StringComparison.OrdinalIgnoreCase)
            || value.Equals("view_file", StringComparison.OrdinalIgnoreCase)
            || value.Equals("open_file", StringComparison.OrdinalIgnoreCase)
            || value.Equals("cat", StringComparison.OrdinalIgnoreCase);
    }


    private static bool StubMatchingResult(JsonArray messages, int assistantIndex, string? callId, string describe)
    {
        if (!string.IsNullOrWhiteSpace(callId))
        {
            for (var i = assistantIndex + 1; i < messages.Count; i++)
            {
                if (messages[i] is not JsonObject result
                    || !LocalToolResultClearing.IsToolResult(result))
                {
                    continue;
                }

                if (string.Equals(result["tool_call_id"]?.ToString(), callId, StringComparison.Ordinal))
                {
                    result["content"] = FormatLoopStub(describe);
                    return true;
                }
            }
        }

        for (var i = assistantIndex + 1; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject next)
            {
                continue;
            }

            if (LocalToolResultClearing.IsToolResult(next))
            {
                next["content"] = FormatLoopStub(describe);
                return true;
            }

            if (RoleOf(next) == "assistant")
            {
                break;
            }
        }

        return false;
    }

    private static string FormatLoopStub(string describe)
    {
        var detail = string.IsNullOrWhiteSpace(describe) ? string.Empty : " " + describe;
        var body = LocalToolResultClearing.OmittedMark
            + "; this call already ran earlier in the conversation" + detail
            + " — see the earlier result. Do not re-run it]";
        return "<tool_response>" + body + "</tool_response>";
    }

    private static string ReadArgField(JsonNode? args, params string[] keys)
    {
        if (args is null)
        {
            return string.Empty;
        }

        if (args is JsonObject obj)
        {
            return FirstNonEmpty(keys.Select(key => obj[key]?.ToString()).ToArray());
        }

        var text = args.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        try
        {
            if (JsonNode.Parse(text) is JsonObject parsed)
            {
                return FirstNonEmpty(keys.Select(key => parsed[key]?.ToString()).ToArray());
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return string.Empty;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string RoleOf(JsonObject message)
        => (message["role"]?.ToString() ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// A cheap, stable fingerprint for duplicate detection: role + content length + a hash of the
    /// content value. This avoids the per-message full ToJsonString() cost that made dedupe slow on
    /// large tool-heavy payloads. Two messages with the same fingerprint are *candidates*; the
    /// caller confirms with a full JSON comparison. The content is read as a raw string (the
    /// dominant cost for tool-result / user messages) rather than re-serializing the whole JSON
    /// object, which is the expensive part.
    /// </summary>
    private static string CheapFingerprint(JsonObject message)
    {
        var role = RoleOf(message);
        var content = message["content"];
        string contentText;
        if (content is JsonValue value)
        {
            contentText = value.ToString() ?? string.Empty;
        }
        else if (content is JsonArray parts)
        {
            // For multi-part content (e.g. image + text), hash the part count + a hash of each
            // part's type. We do NOT serialize image blobs (the expensive part); image-bearing
            // messages are rare duplicates anyway, and a collision is confirmed by the full
            // comparison.
            var sb = new System.Text.StringBuilder(parts.Count.ToString());
            foreach (var part in parts.OfType<JsonObject>())
            {
                sb.Append('|').Append(part["type"]?.ToString() ?? string.Empty);
            }

            contentText = sb.ToString();
        }
        else
        {
            contentText = content?.ToString() ?? string.Empty;
        }

        var contentHash = StableHash(contentText);
        var toolCalls = message["tool_calls"] is JsonArray tc ? tc.Count : 0;
        return role + "\u0001" + contentText.Length + "\u0001" + contentHash + "\u0001" + toolCalls;
    }

    /// <summary>
    /// A stable, fast 32-bit hash of a string (FNV-1a). Used for the cheap duplicate fingerprint.
    /// Not cryptographic — collisions are handled by the full JSON confirmation in the caller.
    /// </summary>
    private static int StableHash(string text)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var c in text)
            {
                hash ^= c;
                hash *= 16777619;
            }

            return (int)hash;
        }
    }

    private static string ExtractText(JsonNode? content)
    {
        if (content is null)
        {
            return string.Empty;
        }

        if (content is JsonValue value)
        {
            return value.ToString() ?? string.Empty;
        }

        if (content is JsonArray parts)
        {
            var chunks = new List<string>();
            foreach (var part in parts.OfType<JsonObject>())
            {
                var text = part["text"]?.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    chunks.Add(text);
                }
            }

            return string.Join("\n", chunks);
        }

        return content.ToString() ?? string.Empty;
    }

    private static string TruncateHeadTail(string text, int capChars)
    {
        if (text.Length <= capChars)
        {
            return text;
        }

        var half = capChars / 2;
        var head = text[..half];
        var tail = text[^half..];
        var elided = text.Length - head.Length - tail.Length;
        return head + "\n…[" + elided + " chars elided]…\n" + tail;
    }
}


/// <summary>
/// The outcome of a forward-prune run: how much the prompt shrank and what was cut.
/// <see cref="None"/> is the sentinel for "nothing was pruned / already fit."
/// </summary>
public sealed record ForwardPruneResult(
    int BeforeTokens,
    int AfterTokens,
    int TargetTokens,
    bool Pruned,
    IReadOnlyList<string> Stages,
    int DedupedMessages = 0,
    int LoopsCollapsed = 0,
    int LoopResultsStubbed = 0,
    int ResultsTruncated = 0)
{
    public static readonly ForwardPruneResult None = new(0, 0, 0, false, new List<string>());

    public int TokensSaved => Math.Max(0, BeforeTokens - AfterTokens);

    public string DiagnosticsLine
    {
        get
        {
            if (!Pruned || Stages.Count == 0)
            {
                return "forward-prune: nothing to trim (prompt already fits or no waste found)";
            }

            return "forward-prune: " + string.Join("; ", Stages)
                   + " — " + BeforeTokens + " -> " + AfterTokens + " tokens (saved " + TokensSaved + ")";
        }
    }
}

/// <summary>
/// Mutable accumulator for a forward-prune run. Each stage records what it cut; the final
/// <see cref="ToResult"/> snapshots the token delta and whether anything was pruned.
/// </summary>
public sealed class ForwardPruneReport
{
    public int BeforeTokens { get; init; }
    public int TargetTokens { get; init; }
    public int AfterTokens { get; set; }

    public int DedupedMessages { get; set; }
    public int LoopsCollapsed { get; set; }
    public int LoopResultsStubbed { get; set; }
    public int ResultsTruncated { get; set; }

    public List<string> Stages { get; } = new();

    public ForwardPruneResult ToResult()
        => new(
            BeforeTokens: BeforeTokens,
            AfterTokens: AfterTokens,
            TargetTokens: TargetTokens,
            Pruned: Stages.Count > 0,
            Stages: Stages.ToList(),
            DedupedMessages: DedupedMessages,
            LoopsCollapsed: LoopsCollapsed,
            LoopResultsStubbed: LoopResultsStubbed,
            ResultsTruncated: ResultsTruncated);
}

