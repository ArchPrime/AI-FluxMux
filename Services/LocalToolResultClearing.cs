using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Drops bulky older tool <em>results</em> from the pack forwarded to llama-server.
/// The tool call stays, so the local model still sees that the command already ran.
/// The Client app still has the full output.
/// </summary>
public static class LocalToolResultClearing
{
    public const int KeepRecentResults = 16;
    public const int PinLatestShellResults = 4;
    public const int RunawayOmittedResults = 32;
    public const int ClosedLoopRunawayOmittedResults = 256;
    public const int ObserveOnlyMillCount = 8;
    public const int ClosedLoopMillLookback = 6;
    public const double RapidChurnSeconds = 3;
    public const int RapidChurnConsecutive = 4;
    public const int MinResultCharsToClear = 240;
    public const string OmittedMark = "[tool output omitted";
    public const string AlreadyRanHeader = "Already ran:";
    public const int MaxLedgerLines = 24;

    /// <summary>
    /// Omitting older tool bodies is forwarding hygiene. A closed-loop
    /// edit plus read/test/observe chat will pass 32. Running or watching
    /// the project without a fresh edit is still useful work. Halt when
    /// the look is screenshot-only or an echo mill, or when omitted
    /// reaches the closed-loop ceiling. Judge this on the pack from
    /// before Compact so a shorten cannot reclassify.
    /// </summary>
    public static bool ShouldHaltAsToolMill(JsonObject payload, int omitted)
        => ShouldHaltAsToolMill(payload, omitted, PortForwardingRules.Defaults);

    public static bool ShouldHaltAsToolMill(JsonObject payload, int omitted, PortForwardingRules rules)
    {
        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        if (live.ObserveOnlyMillEnabled
            && LocalChatPayloadSignals.RecentToolsAreObserveOnlyMill(payload, live.ObserveOnlyMillCount))
        {
            return true;
        }

        if (omitted < live.RunawayOmittedResults)
        {
            return false;
        }

        if (live.ClosedLoopCeilingEnabled && omitted >= live.ClosedLoopRunawayOmittedResults)
        {
            return true;
        }

        if (!live.MillAtOmittedEnabled)
        {
            return false;
        }

        if (!live.ClosedLoopLookbackEnabled)
        {
            return true;
        }

        if (LocalChatPayloadSignals.RecentToolsAreRepeatMill(payload, live.ClosedLoopMillLookback))
        {
            return true;
        }

        if (LocalChatPayloadSignals.RecentToolsAreClosedLoop(payload, live.ClosedLoopMillLookback)
            || LocalChatPayloadSignals.RecentToolsAreDiverseInvestigation(payload, live.ClosedLoopMillLookback)
            || LocalChatPayloadSignals.RecentToolsAreFileReadInvestigation(payload, live.ClosedLoopMillLookback))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// A closed-loop lookback can stay true while the Client app sends a
    /// new tool every second. That is a mill, not edit/read/test.
    /// </summary>
    public static int NextRapidChurnStreak(int omitted, TimeSpan sincePrevious, int previousStreak)
        => NextRapidChurnStreak(omitted, sincePrevious, previousStreak, PortForwardingRules.Defaults);

    public static int NextRapidChurnStreak(
        int omitted,
        TimeSpan sincePrevious,
        int previousStreak,
        PortForwardingRules rules)
    {
        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        if (!live.RapidChurnEnabled || omitted < live.RunawayOmittedResults)
        {
            return 0;
        }

        if (sincePrevious <= TimeSpan.Zero
            || sincePrevious >= TimeSpan.FromSeconds(live.RapidChurnSeconds))
        {
            return 0;
        }

        return previousStreak + 1;
    }

    public static bool ShouldHaltAsRapidChurn(int streak)
        => ShouldHaltAsRapidChurn(streak, PortForwardingRules.Defaults);

    public static bool ShouldHaltAsRapidChurn(int streak, PortForwardingRules rules)
    {
        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        return live.RapidChurnEnabled && streak >= live.RapidChurnConsecutive;
    }

    public static int ClearOlderResults(JsonObject payload, int keepRecent = KeepRecentResults)
        => ClearOlderResults(payload, PortForwardingRules.Defaults with { KeepRecentResults = keepRecent });

    public static int ClearOlderResults(JsonObject payload, PortForwardingRules rules)
    {
        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        if (!live.OmitEnabled)
        {
            return 0;
        }

        var keepRecent = live.KeepRecentResults;
        if (payload["messages"] is not JsonArray messages || keepRecent < 1)
        {
            return 0;
        }

        var resultIndexes = new List<int>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is JsonObject msg && IsToolResult(msg))
            {
                resultIndexes.Add(i);
            }
        }

        if (resultIndexes.Count <= keepRecent)
        {
            return 0;
        }

        var pinned = new HashSet<int>();
        var keepFrom = Math.Max(0, resultIndexes.Count - keepRecent);
        for (var n = keepFrom; n < resultIndexes.Count; n++)
        {
            pinned.Add(resultIndexes[n]);
        }

        var artifacts = LocalSessionArtifactPolicy.Inspect(messages);
        PinLatestFileBodies(messages, resultIndexes, pinned, artifacts);
        PinLatestPictures(messages, resultIndexes, pinned, live.MaxForwardedImages, artifacts);
        PinLatestShellResultsForClosedLoop(messages, resultIndexes, pinned, live.PinLatestShellResults);

        var cleared = 0;
        for (var n = 0; n < resultIndexes.Count; n++)
        {
            var index = resultIndexes[n];
            if (pinned.Contains(index) || messages[index] is not JsonObject result)
            {
                continue;
            }

            if (!ShouldClear(result, live.MinResultCharsToClear))
            {
                continue;
            }

            var describe = DescribeMatchingCall(messages, index);
            result["content"] = FormatStub(result, describe, HasImagePart(result["content"]));
            cleared++;
        }

        return cleared;
    }

    public static IReadOnlyList<string> CollectAlreadyRanLines(JsonArray messages, int endExclusive)
    {
        var entries = new List<(string Line, int Count)>();
        var last = Math.Clamp(endExclusive, 0, messages.Count);
        for (var i = 0; i < last; i++)
        {
            if (messages[i] is not JsonObject message)
            {
                continue;
            }

            foreach (var line in DescribeMessageTools(message))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (entries.Count > 0 && string.Equals(entries[^1].Line, line, StringComparison.Ordinal))
                {
                    entries[^1] = (line, entries[^1].Count + 1);
                    continue;
                }

                entries.Add((line, 1));
            }
        }

        if (entries.Count > MaxLedgerLines)
        {
            entries = entries.GetRange(entries.Count - MaxLedgerLines, MaxLedgerLines);
        }

        return entries.Select(FormatLedgerEntry).ToList();
    }

    public static string FormatLedgerEntry(string line, int count)
        => count <= 1 ? "- " + line : "- " + line + " (" + count + " times)";

    public static string DescribeToolCall(JsonObject call)
    {
        var name = ToolName(call);
        var args = call["function"]?["arguments"] ?? call["arguments"];
        var detail = FirstNonEmpty(
            ReadArgField(args, "command", "cmd", "input"),
            ReadArgField(args, "path", "file_path", "file", "target", "target_file"));
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(detail))
        {
            return name + ": " + Truncate(CollapseWhitespace(detail), 80);
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        return string.IsNullOrWhiteSpace(detail) ? string.Empty : CollapseWhitespace(detail);
    }

    public static bool IsToolResult(JsonObject? message)
    {
        if (message is null)
        {
            return false;
        }

        var role = RoleOf(message);
        if (role is "tool" or "function")
        {
            return true;
        }

        if (role != "user")
        {
            return false;
        }

        var text = ExtractText(message["content"]).TrimStart();
        return text.StartsWith("<tool_response>", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Tool result", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Tool output", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> DescribeMessageTools(JsonObject message)
    {
        var found = false;
        if (message["tool_calls"] is JsonArray calls)
        {
            foreach (var node in calls.OfType<JsonObject>())
            {
                var line = DescribeToolCall(node);
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                found = true;
                yield return line;
            }
        }

        if (found)
        {
            yield break;
        }

        foreach (var command in LocalChatPayloadSignals.ListXmlShellCommands(ExtractText(message["content"])))
        {
            yield return "execute_command: " + Truncate(command, 80);
        }
    }

    private static readonly string[] FileBodyTools =
    [
        "read_file",
        "write_to_file",
        "replace_in_file"
    ];

    private static void PinLatestFileBodies(
        JsonArray messages,
        List<int> resultIndexes,
        HashSet<int> pinned,
        LocalSessionArtifacts artifacts)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var n = resultIndexes.Count - 1; n >= 0; n--)
        {
            var index = resultIndexes[n];
            if (messages[index] is not JsonObject result || HasImagePart(result["content"]))
            {
                continue;
            }

            if (!TryFileBodyKey(messages, index, out var key, out var path) || !seen.Add(key))
            {
                continue;
            }

            if (artifacts.IsArtifact(path))
            {
                continue;
            }

            pinned.Add(index);
        }
    }

    private static bool TryFileBodyKey(JsonArray messages, int resultIndex, out string key, out string path)
    {
        key = string.Empty;
        path = string.Empty;
        var call = FindMatchingCall(messages, resultIndex);
        if (call is null)
        {
            return false;
        }

        var name = ToolName(call);
        if (!FileBodyTools.Any(tool => tool.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        path = FirstNonEmpty(
            ReadArgField(call["function"]?["arguments"] ?? call["arguments"], "path", "file_path", "file", "target", "target_file"));
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        key = name + "|" + path.Trim();
        return true;
    }

    private static readonly Regex TestOrBuildCommandRegex = new(
        @"\b(test|spec|pytest|jest|vitest|mocha|phpunit|nunit|xunit|playwright|cypress|msbuild|lint|build)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static void PinLatestShellResultsForClosedLoop(
        JsonArray messages,
        List<int> resultIndexes,
        HashSet<int> pinned,
        int pinLatestShell = PinLatestShellResults)
    {
        var testPinned = false;
        var buildPinned = false;
        var recent = 0;
        for (var n = resultIndexes.Count - 1; n >= 0; n--)
        {
            var index = resultIndexes[n];
            if (messages[index] is not JsonObject || HasImagePart(messages[index]!["content"]))
            {
                continue;
            }

            var call = FindMatchingCall(messages, index);
            if (call is null)
            {
                continue;
            }

            var name = ToolName(call);
            if (!IsShellTool(name))
            {
                continue;
            }

            var command = FirstNonEmpty(
                ReadArgField(call["function"]?["arguments"] ?? call["arguments"],
                    "command", "cmd", "input"));
            var isTest = LooksLikeTestCommand(command);
            var isBuild = !isTest && LooksLikeBuildCommand(command);
            var keep = false;
            if (isTest && !testPinned)
            {
                keep = true;
                testPinned = true;
            }
            else if (isBuild && !buildPinned)
            {
                keep = true;
                buildPinned = true;
            }
            else if (recent < pinLatestShell)
            {
                keep = true;
                recent++;
            }

            if (keep)
            {
                pinned.Add(index);
            }

            if (testPinned && buildPinned && recent >= pinLatestShell)
            {
                break;
            }
        }
    }

    private static bool IsShellTool(string name)
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

    private static bool LooksLikeTestCommand(string command)
        => !string.IsNullOrWhiteSpace(command)
           && (command.Contains("test", StringComparison.OrdinalIgnoreCase)
               || command.Contains("pytest", StringComparison.OrdinalIgnoreCase)
               || command.Contains("jest", StringComparison.OrdinalIgnoreCase)
               || command.Contains("vitest", StringComparison.OrdinalIgnoreCase)
               || command.Contains("playwright", StringComparison.OrdinalIgnoreCase)
               || command.Contains("cypress", StringComparison.OrdinalIgnoreCase)
               || command.Contains("spec", StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeBuildCommand(string command)
        => !string.IsNullOrWhiteSpace(command)
           && TestOrBuildCommandRegex.IsMatch(command)
           && !LooksLikeTestCommand(command);

    private static void PinLatestPictures(
        JsonArray messages,
        List<int> resultIndexes,
        HashSet<int> pinned,
        int maxPictures,
        LocalSessionArtifacts artifacts)
    {
        if (maxPictures < 1)
        {
            return;
        }

        var kept = 0;
        for (var n = resultIndexes.Count - 1; n >= 0 && kept < maxPictures; n--)
        {
            var index = resultIndexes[n];
            if (messages[index] is not JsonObject result || !HasImagePart(result["content"]))
            {
                continue;
            }

            var call = FindMatchingCall(messages, index);
            var path = call is null ? string.Empty : LocalSessionArtifactPolicy.ReadToolPath(call);
            if (!LocalSessionArtifactPolicy.IsObservationTool(call)
                && artifacts.IsArtifact(path))
            {
                continue;
            }

            pinned.Add(index);
            kept++;
        }
    }

    public static JsonObject? FindMatchingCall(JsonArray messages, int resultIndex)
    {
        if (messages[resultIndex] is not JsonObject result)
        {
            return null;
        }

        var callId = result["tool_call_id"]?.ToString();
        for (var i = resultIndex - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject prior
                || !RoleOf(prior).Equals("assistant", StringComparison.OrdinalIgnoreCase)
                || prior["tool_calls"] is not JsonArray calls)
            {
                continue;
            }

            foreach (var node in calls.OfType<JsonObject>())
            {
                var id = node["id"]?.ToString();
                if (!string.IsNullOrWhiteSpace(callId)
                    && !string.Equals(id, callId, StringComparison.Ordinal))
                {
                    continue;
                }

                return node;
            }

            break;
        }

        return null;
    }

    private static string DescribeMatchingCall(JsonArray messages, int resultIndex)
        => FindMatchingCall(messages, resultIndex) is JsonObject call
            ? DescribeToolCall(call)
            : string.Empty;

    private static bool ShouldClear(JsonObject result, int minResultChars = MinResultCharsToClear)
    {
        var text = ExtractText(result["content"]);
        if (text.StartsWith(OmittedMark, StringComparison.Ordinal))
        {
            return false;
        }

        if (HasImagePart(result["content"]))
        {
            return true;
        }

        return text.Length >= minResultChars;
    }

    private static string FormatStub(JsonObject result, string describe, bool hadPicture)
    {
        string body;
        if (hadPicture)
        {
            body = OmittedMark + "; picture was not forwarded to llama-server. This Client-app chat can continue — attach that frame in the Client app if llama-server needs it]";
        }
        else if (string.IsNullOrWhiteSpace(describe))
        {
            body = OmittedMark + "; the call already ran]";
        }
        else
        {
            body = OmittedMark + "; already ran " + describe + "]";
        }
        var original = ExtractText(result["content"]).TrimStart();
        if (original.StartsWith("<tool_response>", StringComparison.OrdinalIgnoreCase))
        {
            return "<tool_response>" + body + "</tool_response>";
        }

        return body;
    }

    private static string FormatLedgerEntry((string Line, int Count) entry)
        => FormatLedgerEntry(entry.Line, entry.Count);

    private static string ToolName(JsonObject call)
        => (call["function"]?["name"]?.ToString()
            ?? call["name"]?.ToString()
            ?? string.Empty).Trim();

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

    private static bool HasImagePart(JsonNode? node)
    {
        if (node is JsonObject part
            && (IsImageType(part["type"]?.ToString())
                || part["image_url"] is not null
                || (part["source"]?["media_type"]?.ToString() ?? string.Empty)
                    .StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (node is JsonArray items)
        {
            return items.Any(HasImagePart);
        }

        return false;
    }

    private static bool IsImageType(string? type)
        => !string.IsNullOrWhiteSpace(type)
           && (type.Equals("image", StringComparison.OrdinalIgnoreCase)
               || type.Equals("image_url", StringComparison.OrdinalIgnoreCase)
               || type.Contains("image", StringComparison.OrdinalIgnoreCase));

    private static string RoleOf(JsonObject message)
        => (message["role"]?.ToString() ?? string.Empty).Trim().ToLowerInvariant();

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

    private static string CollapseWhitespace(string text)
        => string.Join(" ", (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max];
}
