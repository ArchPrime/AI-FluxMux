using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Promotes text-form llama-server tool calls into OpenAI <c>tool_calls</c>
/// before the Client app sees them. Only names the Client app declared are
/// promoted, so healing cannot invent a tool. Structured grammar-mode calls
/// stay byte-identical except for argument JSON that is not valid.
/// </summary>
public static class LocalToolCallHealing
{
    public const string CallIdPrefix = "call_heal_";

    private static readonly Regex ToolCallBlockRegex = new(
        @"<tool_call>(?<body>[\s\S]*?)(?:</tool_call>|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GemmaToolCallRegex = new(
        @"<\|tool_call\|?>(?<body>[\s\S]*?)(?:<\|/?tool_call\|>|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FunctionEqualsRegex = new(
        @"<function=(?<name>[A-Za-z0-9_\-\.]+)>(?<body>[\s\S]*?)(?:</function>|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ParameterEqualsRegex = new(
        @"<parameter=(?<key>[A-Za-z0-9_\-\.]+)>(?<value>[\s\S]*?)</parameter>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex XmlArgRegex = new(
        @"<(?<key>[A-Za-z][A-Za-z0-9_\-\.]*)>(?<value>[\s\S]*?)</\k<key>>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UnquotedKeyRegex = new(
        @"(^|\{|,)\s*([A-Za-z_][A-Za-z0-9_]*)\s*:",
        RegexOptions.Compiled);

    private static readonly Regex TrailingCommaRegex = new(
        @",(\s*[}\]])",
        RegexOptions.Compiled);

    private static readonly string[] MarkupNeedles =
    [
        "<tool_call",
        "<|tool_call",
        "<function=",
        "[TOOL_CALL",
    ];

    public readonly record struct HealStats(int Promoted, int Coerced, int Deduped, int Dropped = 0)
    {
        public bool Changed => Promoted > 0 || Coerced > 0 || Deduped > 0 || Dropped > 0;

        public string FormatLog()
        {
            var parts = new List<string>();
            if (Promoted > 0)
            {
                parts.Add("promoted " + Promoted.ToString(CultureInfo.InvariantCulture));
            }

            if (Coerced > 0)
            {
                parts.Add("coerced " + Coerced.ToString(CultureInfo.InvariantCulture) + " argument(s)");
            }

            if (Deduped > 0)
            {
                parts.Add("dropped " + Deduped.ToString(CultureInfo.InvariantCulture) + " duplicate call(s)");
            }

            if (Dropped > 0)
            {
                parts.Add("blocked " + Dropped.ToString(CultureInfo.InvariantCulture) + " diagnostic write(s)");
            }

            if (parts.Count == 0)
            {
                return "healed malformed tool call(s)";
            }

            return string.Join(", ", parts);
        }
    }

    public static IReadOnlyList<string> CollectDeclaredToolNames(JsonObject? payload)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddDeclaredNames(payload?["tools"], names);
        AddDeclaredNames(payload?["functions"], names);
        return names.Count == 0 ? Array.Empty<string>() : names.ToList();
    }

    public static bool AllowsParallelToolCalls(JsonObject? payload)
    {
        if (payload?["parallel_tool_calls"] is JsonValue flag
            && flag.TryGetValue<bool>(out var allowed))
        {
            return allowed;
        }

        return true;
    }

    public static bool LooksLikeToolMarkup(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var needle in MarkupNeedles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static int IndexOfToolMarkup(string text)
        => IndexOfToolMarkup(text, visibleOnly: false);

    public static int IndexOfVisibleToolMarkup(string text)
        => IndexOfToolMarkup(text, visibleOnly: true);

    public static int IndexOfToolMarkup(string text, bool visibleOnly)
    {
        if (string.IsNullOrEmpty(text))
        {
            return -1;
        }

        var i = 0;
        while (i < text.Length)
        {
            if (visibleOnly && StartsThink(text, i, out var thinkLen, out var thinkClose))
            {
                if (thinkClose < 0)
                {
                    return -1;
                }

                i = thinkClose;
                continue;
            }

            foreach (var needle in MarkupNeedles)
            {
                if (text.AsSpan(i).StartsWith(needle, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            i++;
        }

        return -1;
    }

    private static bool StartsThink(string text, int index, out int openLength, out int closeIndex)
    {
        openLength = 0;
        closeIndex = -1;
        if (StartsAt(text, index, "<think>", out openLength)
            || StartsAt(text, index, "<thinking>", out openLength))
        {
            var closeTag = openLength == 7 ? "</think>" : "</thinking>";
            var close = text.IndexOf(closeTag, index + openLength, StringComparison.OrdinalIgnoreCase);
            closeIndex = close < 0 ? -1 : close + closeTag.Length;
            return true;
        }

        return false;
    }

    private static bool StartsAt(string text, int index, string value, out int length)
    {
        length = value.Length;
        return text.AsSpan(index).StartsWith(value, StringComparison.OrdinalIgnoreCase);
    }

    public static bool HealCompletion(
        JsonObject root,
        IReadOnlyCollection<string> declaredTools,
        bool allowParallel = true,
        LocalSessionArtifacts? artifacts = null)
    {
        return HealCompletion(root, declaredTools, allowParallel, out _, artifacts);
    }

    public static bool HealCompletion(
        JsonObject root,
        IReadOnlyCollection<string> declaredTools,
        bool allowParallel,
        out HealStats stats,
        LocalSessionArtifacts? artifacts = null)
    {
        stats = default;
        if (declaredTools is not { Count: > 0 } || root["choices"] is not JsonArray choices)
        {
            return false;
        }

        var declared = ToSet(declaredTools);
        var promoted = 0;
        var coerced = 0;
        var deduped = 0;
        var dropped = 0;
        var changed = false;
        foreach (var node in choices)
        {
            if (node is not JsonObject choice)
            {
                continue;
            }

            if (HealMessage(choice["message"] as JsonObject, declared, allowParallel, artifacts, ref promoted, ref coerced, ref deduped, ref dropped))
            {
                changed = true;
                if (promoted > 0)
                {
                    choice["finish_reason"] = "tool_calls";
                }
            }

            if (HealMessage(choice["delta"] as JsonObject, declared, allowParallel, artifacts, ref promoted, ref coerced, ref deduped, ref dropped))
            {
                changed = true;
                if (promoted > 0)
                {
                    choice["finish_reason"] = "tool_calls";
                }
            }
        }

        stats = new HealStats(promoted, coerced, deduped, dropped);
        return changed;
    }

    public static bool HealMessage(
        JsonObject? message,
        IReadOnlyCollection<string> declaredTools,
        bool allowParallel = true,
        LocalSessionArtifacts? artifacts = null)
    {
        var promoted = 0;
        var coerced = 0;
        var deduped = 0;
        var dropped = 0;
        return HealMessage(message, ToSet(declaredTools), allowParallel, artifacts, ref promoted, ref coerced, ref deduped, ref dropped);
    }

    public static IReadOnlyList<ParsedToolCall> ParseTextCalls(string? content, IReadOnlyCollection<string> declaredTools)
        => ParseTextCalls(content, ToSet(declaredTools));

    public static string? CoerceArgumentsJson(string? raw, string? toolName = null)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return "{}";
        }

        if (TryCanonicalJsonObject(text, out var canonical))
        {
            return canonical;
        }

        var unfenced = UnwrapFence(text);
        if (!string.Equals(unfenced, text, StringComparison.Ordinal)
            && TryCanonicalJsonObject(unfenced, out canonical))
        {
            return canonical;
        }

        var repaired = TrailingCommaRegex.Replace(unfenced, "$1");
        repaired = QuoteUnquotedKeys(repaired);
        if (!string.Equals(repaired, unfenced, StringComparison.Ordinal)
            && TryCanonicalJsonObject(repaired, out canonical))
        {
            return canonical;
        }

        if (unfenced.Contains('\'', StringComparison.Ordinal))
        {
            var swapped = QuoteUnquotedKeys(TrailingCommaRegex.Replace(SwapJsonSingleQuotes(unfenced), "$1"));
            if (TryCanonicalJsonObject(swapped, out canonical))
            {
                return canonical;
            }
        }

        var xmlArgs = ParseXmlArguments(unfenced);
        if (xmlArgs is not null)
        {
            return xmlArgs.ToJsonString();
        }

        if (LooksLikeBareObject(unfenced) && TryCanonicalJsonObject("{" + unfenced + "}", out canonical))
        {
            return canonical;
        }

        return WrapBareValue(unfenced, toolName);
    }

    public readonly record struct ParsedToolCall(string Name, string ArgumentsJson, int Start, int Length);

    private static bool HealMessage(
        JsonObject? message,
        HashSet<string> declared,
        bool allowParallel,
        LocalSessionArtifacts? artifacts,
        ref int promoted,
        ref int coerced,
        ref int deduped,
        ref int dropped)
    {
        if (message is null || declared.Count == 0)
        {
            return false;
        }

        var changed = false;
        var existing = message["tool_calls"] as JsonArray;
        var hadStructured = existing is { Count: > 0 };
        if (hadStructured)
        {
            coerced += CoerceCallArguments(existing!);
            if (coerced > 0)
            {
                changed = true;
            }
        }

        var content = ReadText(message["content"]);
        if (!hadStructured && !string.IsNullOrWhiteSpace(content))
        {
            var parsed = ParseTextCalls(content, declared);
            if (parsed.Count > 0)
            {
                var calls = new JsonArray();
                foreach (var call in parsed)
                {
                    calls.Add(ToOpenAiCall(call.Name, call.ArgumentsJson, calls.Count + 1));
                }

                message["tool_calls"] = calls;
                existing = calls;
                promoted += parsed.Count;
                message["content"] = StripParsedMarkup(content, parsed);
                changed = true;
            }
        }
        else if (hadStructured && LooksLikeToolMarkup(content))
        {
            var leftover = StripParsedMarkup(content, ParseTextCalls(content, declared));
            if (!string.Equals(leftover, content, StringComparison.Ordinal))
            {
                message["content"] = leftover;
                changed = true;
            }
        }

        if (existing is { Count: > 0 })
        {
            var removed = DedupAndCap(existing, allowParallel);
            if (removed > 0)
            {
                deduped += removed;
                changed = true;
            }

            if (existing.Count == 0)
            {
                message.Remove("tool_calls");
            }
        }

        var blocked = LocalSessionArtifactPolicy.FilterMessageToolCalls(message, artifacts);
        if (blocked > 0)
        {
            dropped += blocked;
            changed = true;
        }

        return changed;
    }

    private static int CoerceCallArguments(JsonArray calls)
    {
        var coerced = 0;
        foreach (var node in calls.OfType<JsonObject>())
        {
            var fn = node["function"] as JsonObject ?? node;
            var name = fn["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (fn["arguments"] is JsonObject objectArgs)
            {
                fn["arguments"] = objectArgs.ToJsonString();
                coerced++;
                continue;
            }

            var raw = fn["arguments"]?.ToString();
            if (string.IsNullOrEmpty(raw))
            {
                continue;
            }

            if (TryCanonicalJsonObject(raw, out _))
            {
                continue;
            }

            var coercedJson = CoerceArgumentsJson(raw, name);
            if (!string.IsNullOrEmpty(coercedJson)
                && !string.Equals(coercedJson, raw, StringComparison.Ordinal))
            {
                fn["arguments"] = coercedJson;
                coerced++;
            }
        }

        return coerced;
    }

    private static int DedupAndCap(JsonArray calls, bool allowParallel)
    {
        var removed = 0;
        if (!allowParallel && calls.Count > 1)
        {
            removed += calls.Count - 1;
            while (calls.Count > 1)
            {
                calls.RemoveAt(calls.Count - 1);
            }

            return removed;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < calls.Count; i++)
        {
            if (calls[i] is not JsonObject call)
            {
                continue;
            }

            var key = CallKey(call);
            if (seen.Add(key))
            {
                continue;
            }

            calls.RemoveAt(i);
            i--;
            removed++;
        }

        return removed;
    }

    private static string CallKey(JsonObject call)
    {
        var fn = call["function"] as JsonObject ?? call;
        var name = (fn["name"]?.ToString() ?? string.Empty).Trim().ToLowerInvariant();
        var args = fn["arguments"]?.ToString() ?? string.Empty;
        if (TryCanonicalJsonObject(args, out var canonical))
        {
            args = canonical;
        }

        return name + "\n" + args;
    }

    private static List<ParsedToolCall> ParseTextCalls(string? content, HashSet<string> declared)
    {
        var found = new List<ParsedToolCall>();
        if (string.IsNullOrWhiteSpace(content) || declared.Count == 0)
        {
            return found;
        }

        var parseText = content;
        var thinkSpans = ThinkSpans(parseText);
        CollectBlocks(parseText, ToolCallBlockRegex, declared, found, thinkSpans);
        CollectBlocks(parseText, GemmaToolCallRegex, declared, found, thinkSpans);
        CollectNamedFunctionBlocks(parseText, declared, found, thinkSpans);
        CollectDeclaredXmlRoots(parseText, declared, found, thinkSpans);
        found.Sort((left, right) => left.Start.CompareTo(right.Start));
        return DedupeParsed(found);
    }

    private static void CollectBlocks(
        string text,
        Regex regex,
        HashSet<string> declared,
        List<ParsedToolCall> found,
        List<(int Start, int End)> thinkSpans)
    {
        foreach (Match match in regex.Matches(text))
        {
            if (InsideThink(match.Index, thinkSpans)
                || !TryParseCallBody(match.Groups["body"].Value, declared, out var name, out var args)
                || Overlaps(found, match.Index, match.Length))
            {
                continue;
            }

            found.Add(new ParsedToolCall(name, args, match.Index, match.Length));
        }
    }

    private static void CollectNamedFunctionBlocks(
        string text,
        HashSet<string> declared,
        List<ParsedToolCall> found,
        List<(int Start, int End)> thinkSpans)
    {
        foreach (Match match in FunctionEqualsRegex.Matches(text))
        {
            var name = match.Groups["name"].Value.Trim();
            if (InsideThink(match.Index, thinkSpans)
                || !declared.Contains(name)
                || Overlaps(found, match.Index, match.Length))
            {
                continue;
            }

            var args = CoerceArgumentsJson(match.Groups["body"].Value, name) ?? "{}";
            found.Add(new ParsedToolCall(name, args, match.Index, match.Length));
        }
    }

    private static void CollectDeclaredXmlRoots(
        string text,
        HashSet<string> declared,
        List<ParsedToolCall> found,
        List<(int Start, int End)> thinkSpans)
    {
        foreach (var name in declared)
        {
            if (string.IsNullOrWhiteSpace(name) || !IsSafeTagName(name))
            {
                continue;
            }

            var pattern = @"<(?<tag>" + Regex.Escape(name) + @")\b[\s\S]*?</\k<tag>>";
            MatchCollection matches;
            try
            {
                matches = Regex.Matches(text, pattern, RegexOptions.IgnoreCase);
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }

            foreach (Match match in matches)
            {
                if (InsideThink(match.Index, thinkSpans) || Overlaps(found, match.Index, match.Length))
                {
                    continue;
                }

                var inner = InnerXml(match.Value);
                var args = CoerceArgumentsJson(inner, name) ?? "{}";
                found.Add(new ParsedToolCall(name, args, match.Index, match.Length));
            }
        }
    }

    private static List<(int Start, int End)> ThinkSpans(string text)
    {
        var spans = new List<(int Start, int End)>();
        var i = 0;
        while (i < text.Length)
        {
            if (!StartsThink(text, i, out _, out var close))
            {
                i++;
                continue;
            }

            spans.Add((i, close < 0 ? text.Length : close));
            i = close < 0 ? text.Length : close;
        }

        return spans;
    }

    private static bool InsideThink(int index, List<(int Start, int End)> spans)
    {
        foreach (var (start, end) in spans)
        {
            if (index >= start && index < end)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseCallBody(string body, HashSet<string> declared, out string name, out string args)
    {
        name = string.Empty;
        args = "{}";
        var text = (body ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return false;
        }

        var jsonStart = text.IndexOf('{');
        if (jsonStart >= 0 && TryExtractJsonObject(text, jsonStart, out var json))
        {
            if (TryReadNamedJson(json, declared, out name, out args))
            {
                return true;
            }
        }

        var firstLine = FirstToken(text);
        if (declared.Contains(firstLine))
        {
            name = firstLine;
            var rest = text[firstLine.Length..].Trim();
            args = CoerceArgumentsJson(rest, name) ?? "{}";
            return true;
        }

        return false;
    }

    private static bool TryReadNamedJson(string json, HashSet<string> declared, out string name, out string args)
    {
        name = string.Empty;
        args = "{}";
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch
        {
            var coerced = CoerceArgumentsJson(json);
            if (coerced is null)
            {
                return false;
            }

            try
            {
                node = JsonNode.Parse(coerced);
            }
            catch
            {
                return false;
            }
        }

        if (node is not JsonObject obj)
        {
            return false;
        }

        var rawName = (obj["name"] ?? obj["function"]?["name"] ?? obj["tool"])?.ToString();
        if (string.IsNullOrWhiteSpace(rawName) || !declared.Contains(rawName))
        {
            return false;
        }

        name = rawName.Trim();
        if (obj["arguments"] is JsonNode arguments)
        {
            args = arguments is JsonValue value && value.TryGetValue<string>(out var asString)
                ? CoerceArgumentsJson(asString, name) ?? "{}"
                : arguments.ToJsonString();
        }
        else if (obj["parameters"] is JsonNode parameters)
        {
            args = parameters.ToJsonString();
        }
        else
        {
            var copy = obj.DeepClone() as JsonObject ?? new JsonObject();
            copy.Remove("name");
            copy.Remove("tool");
            copy.Remove("function");
            args = copy.Count == 0 ? "{}" : copy.ToJsonString();
        }

        if (!TryCanonicalJsonObject(args, out var canonical))
        {
            args = CoerceArgumentsJson(args, name) ?? "{}";
        }
        else
        {
            args = canonical;
        }

        return true;
    }

    private static JsonObject ToOpenAiCall(string name, string argumentsJson, int index)
        => new()
        {
            ["id"] = CallIdPrefix + index.ToString(CultureInfo.InvariantCulture),
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["arguments"] = argumentsJson
            }
        };

    private static string StripParsedMarkup(string content, IReadOnlyList<ParsedToolCall> parsed)
    {
        if (parsed.Count == 0)
        {
            return content.Trim();
        }

        var builder = new StringBuilder(content);
        foreach (var call in parsed.OrderByDescending(item => item.Start))
        {
            var start = Math.Clamp(call.Start, 0, builder.Length);
            var length = Math.Clamp(call.Length, 0, builder.Length - start);
            builder.Remove(start, length);
        }

        return CollapseExtraBlankLines(builder.ToString());
    }

    private static string CollapseExtraBlankLines(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return Regex.Replace(trimmed, @"\n{3,}", "\n\n");
    }

    private static JsonObject? ParseXmlArguments(string text)
    {
        var obj = new JsonObject();
        foreach (Match match in ParameterEqualsRegex.Matches(text))
        {
            obj[match.Groups["key"].Value] = match.Groups["value"].Value.Trim();
        }

        if (obj.Count > 0)
        {
            return obj;
        }

        foreach (Match match in XmlArgRegex.Matches(text))
        {
            var key = match.Groups["key"].Value;
            if (key.Equals("tool_call", StringComparison.OrdinalIgnoreCase)
                || key.Equals("function", StringComparison.OrdinalIgnoreCase)
                || key.Equals("think", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            obj[key] = match.Groups["value"].Value.Trim();
        }

        return obj.Count == 0 ? null : obj;
    }

    private static string? WrapBareValue(string text, string? toolName)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "{}";
        }

        var name = (toolName ?? string.Empty).Trim();
        if (name.Equals("execute_command", StringComparison.OrdinalIgnoreCase)
            || name.Equals("run_command", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject { ["command"] = text }.ToJsonString();
        }

        if (name.Equals("read_file", StringComparison.OrdinalIgnoreCase)
            || name.Equals("list_files", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject { ["path"] = text }.ToJsonString();
        }

        if (name.Equals("write_to_file", StringComparison.OrdinalIgnoreCase)
            || name.Equals("replace_in_file", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject { ["path"] = text }.ToJsonString();
        }

        return new JsonObject { ["input"] = text }.ToJsonString();
    }

    private static bool TryCanonicalJsonObject(string text, out string canonical)
    {
        canonical = string.Empty;
        try
        {
            if (JsonNode.Parse(text) is not JsonNode node)
            {
                return false;
            }

            if (node is JsonValue value && value.TryGetValue<string>(out var nested))
            {
                return TryCanonicalJsonObject(nested, out canonical);
            }

            if (node is not JsonObject && node is not JsonArray)
            {
                return false;
            }

            canonical = node.ToJsonString();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractJsonObject(string text, int start, out string json)
    {
        json = string.Empty;
        if (start < 0 || start >= text.Length || text[start] != '{')
        {
            return false;
        }

        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (escape)
                {
                    escape = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escape = true;
                    continue;
                }

                if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth != 0)
                {
                    continue;
                }

                json = text[start..(i + 1)];
                return TryCanonicalJsonObject(json, out var canonical)
                    ? ((json = canonical) != null)
                    : CoerceArgumentsJson(json) is { } coerced && ((json = coerced) != null);
            }
        }

        var truncated = text[start..];
        var coercedTruncated = CoerceArgumentsJson(truncated + new string('}', 1));
        if (coercedTruncated is not null && TryCanonicalJsonObject(coercedTruncated, out var fixedJson))
        {
            json = fixedJson;
            return true;
        }

        return false;
    }

    private static string SwapJsonSingleQuotes(string text)
    {
        var builder = new StringBuilder(text.Length);
        var inDouble = false;
        var escape = false;
        foreach (var ch in text)
        {
            if (inDouble)
            {
                builder.Append(ch);
                if (escape)
                {
                    escape = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escape = true;
                    continue;
                }

                if (ch == '"')
                {
                    inDouble = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inDouble = true;
                builder.Append(ch);
                continue;
            }

            builder.Append(ch == '\'' ? '"' : ch);
        }

        return builder.ToString();
    }

    private static string QuoteUnquotedKeys(string text)
        => UnquotedKeyRegex.Replace(text, "$1\"$2\":");

    private static string UnwrapFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNl = trimmed.IndexOf('\n');
        if (firstNl < 0)
        {
            return trimmed.Trim('`');
        }

        var body = trimmed[(firstNl + 1)..];
        var close = body.LastIndexOf("```", StringComparison.Ordinal);
        return close >= 0 ? body[..close].Trim() : body.Trim();
    }

    private static bool LooksLikeBareObject(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Contains(':')
            && !trimmed.StartsWith('{')
            && !trimmed.StartsWith('<');
    }

    private static string FirstToken(string text)
    {
        var trimmed = text.TrimStart();
        var end = 0;
        while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]) && trimmed[end] is not '<' and not '{' and not '[')
        {
            end++;
        }

        return end == 0 ? string.Empty : trimmed[..end].Trim();
    }

    private static string InnerXml(string tagged)
    {
        var open = tagged.IndexOf('>');
        var close = tagged.LastIndexOf("</", StringComparison.Ordinal);
        if (open < 0 || close <= open)
        {
            return tagged;
        }

        return tagged[(open + 1)..close].Trim();
    }

    private static bool IsSafeTagName(string name)
        => name.Length > 0 && name.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.');

    private static bool Overlaps(List<ParsedToolCall> found, int start, int length)
    {
        var end = start + length;
        foreach (var call in found)
        {
            var callEnd = call.Start + call.Length;
            if (start < callEnd && end > call.Start)
            {
                return true;
            }
        }

        return false;
    }

    private static List<ParsedToolCall> DedupeParsed(List<ParsedToolCall> found)
    {
        var unique = new List<ParsedToolCall>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var call in found)
        {
            if (seen.Add(call.Name.ToLowerInvariant() + "\n" + call.ArgumentsJson))
            {
                unique.Add(call);
            }
        }

        return unique;
    }

    private static HashSet<string> ToSet(IReadOnlyCollection<string> names)
        => names is HashSet<string> set && set.Comparer == StringComparer.OrdinalIgnoreCase
            ? set
            : new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    private static void AddDeclaredNames(JsonNode? node, HashSet<string> names)
    {
        if (node is not JsonArray array)
        {
            return;
        }

        foreach (var item in array.OfType<JsonObject>())
        {
            var name = (item["function"]?["name"] ?? item["name"])?.ToString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name.Trim());
            }
        }
    }

    private static string ReadText(JsonNode? content)
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
            var builder = new StringBuilder();
            foreach (var part in parts)
            {
                if (part is JsonValue textValue)
                {
                    builder.Append(textValue);
                    continue;
                }

                if (part is JsonObject obj && obj["text"] is JsonNode text)
                {
                    builder.Append(text);
                }
            }

            return builder.ToString();
        }

        return content.ToString() ?? string.Empty;
    }
}
