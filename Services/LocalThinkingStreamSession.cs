using System;
using System.Text;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Stateful strip of thinking spans across SSE deltas. Per-line regex cannot see
/// an opening tag in one chunk and the close in a later chunk, so Reasoning Off
/// otherwise leaks think-body tokens (often with spaces as separate deltas — if
/// those are dropped, Harness shows words jammed together).
/// </summary>
public sealed class LocalThinkingStreamSession
{
    private static readonly string[] OpenTags =
    [
        "<think>",
        "<thinking>",
        "\x3creacted_reasoning\x3e",
    ];
    private static readonly string[] CloseTags =
    [
        "</think>",
        "</thinking>",
        "\x3c/redacted_reasoning\x3e",
    ];

    private bool _insideThink;
    private bool _openedThinkTag;
    private readonly int _maxTokens;
    private readonly StringBuilder _prefix = new();

    public bool StripAllThinking { get; }

    public bool SawThinking { get; private set; }

    public bool SawVisibleContent { get; private set; }

    public bool SawToolCalls { get; private set; }

    public bool InsideThink => _insideThink;

    public bool OpenedThinkTag => _openedThinkTag;

    public bool IsThinkOnly
        => StripAllThinking && SawThinking && !SawVisibleContent && !SawToolCalls;

    public bool IsThinkCutOff
        => !StripAllThinking && SawThinking && !SawVisibleContent && !SawToolCalls;

    public bool WroteThinkOnlyNotice { get; private set; }

    public void MarkThinkOnlyNoticeWritten() => WroteThinkOnlyNotice = true;

    public string? ReasoningMode { get; }

    public LocalThinkingStreamSession(string? reasoningMode, int maxTokens = 0)
    {
        ReasoningMode = reasoningMode;
        _maxTokens = maxTokens;
        var mode = LocalReasoningLaunchPolicy.NormalizeMode(reasoningMode);
        // Thinking on (On / Low / Medium / XHigh): only empty blocks are cleaned
        // elsewhere; keep nonempty think text.
        StripAllThinking = !LocalReasoningLaunchPolicy.IsThinkingEnabled(mode);
    }

    public bool FilterCompletionJson(JsonObject root)
    {
        if (root["choices"] is not JsonArray choices)
        {
            return false;
        }

        var changed = false;
        foreach (var choiceNode in choices)
        {
            if (choiceNode is not JsonObject choice)
            {
                continue;
            }

            if (FilterMessage(choice["delta"] as JsonObject))
            {
                changed = true;
            }

            if (FilterMessage(choice["message"] as JsonObject))
            {
                changed = true;
            }

            if (IsThinkOnly
                && LocalThinkOnlyReply.ApplyNoticeToFinishedChoice(
                    choice,
                    LocalThinkOnlyReply.FormatClientMessage()))
            {
                WroteThinkOnlyNotice = true;
                changed = true;
            }
            else if (IsThinkCutOff
                && LocalThinkOnlyReply.ApplyNoticeToFinishedChoice(
                    choice,
                    LocalThinkBudgetNotice.FormatClientMessage(ReasoningMode, _maxTokens)))
            {
                WroteThinkOnlyNotice = true;
                changed = true;
            }
        }

        return changed;
    }

    private bool FilterMessage(JsonObject? message)
    {
        if (message is null)
        {
            return false;
        }

        var changed = false;
        if (message["tool_calls"] is JsonArray { Count: > 0 }
            || message["function_call"] is JsonObject)
        {
            SawToolCalls = true;
        }

        if (StripAllThinking)
        {
            if (message.ContainsKey("reasoning_content"))
            {
                message.Remove("reasoning_content");
                SawThinking = true;
                changed = true;
            }

            if (message.ContainsKey("reasoning"))
            {
                message.Remove("reasoning");
                SawThinking = true;
                changed = true;
            }
        }
        else if (message.ContainsKey("reasoning_content")
            || message.ContainsKey("reasoning"))
        {
            SawThinking = true;
            _insideThink = true;
        }

        if (message["content"] is JsonValue contentValue
            && contentValue.TryGetValue<string>(out var content)
            && content.Length > 0)
        {
            var filtered = FilterText(content);
            if (!string.Equals(filtered, content, StringComparison.Ordinal))
            {
                message["content"] = filtered;
                changed = true;
            }
        }

        return changed;
    }

    public string FilterText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        if (!StripAllThinking)
        {
            // Per-chunk empty-think cleanup Trim() ate space-only deltas, so
            // Harness/Cline showed words jammed together inside <think>.
            NoteThinkProgress(text);
            return text;
        }

        var input = _prefix.Length == 0 ? text : _prefix + text;
        _prefix.Clear();
        var output = new StringBuilder(input.Length);
        var startedInsideThink = _insideThink;
        var i = 0;
        while (i < input.Length)
        {
            if (!_insideThink)
            {
                var open = IndexOfAnyTag(input, i, OpenTags, out var openTag);
                if (open < 0)
                {
                    var hold = HoldPartialTagPrefix(input, i, OpenTags);
                    if (hold > 0)
                    {
                        output.Append(input, i, input.Length - i - hold);
                        _prefix.Append(input, input.Length - hold, hold);
                        break;
                    }

                    output.Append(input, i, input.Length - i);
                    break;
                }

                output.Append(input, i, open - i);
                _insideThink = true;
                SawThinking = true;
                i = open + openTag.Length;
                continue;
            }

            var close = IndexOfAnyTag(input, i, CloseTags, out var closeTag);
            if (close < 0)
            {
                var hold = HoldPartialTagPrefix(input, i, CloseTags);
                if (hold > 0)
                {
                    _prefix.Append(input, input.Length - hold, hold);
                }

                // Drop think-body (including space-only deltas).
                break;
            }

            _insideThink = false;
            i = close + closeTag.Length;
        }

        var filtered = output.ToString();
        if (startedInsideThink || _insideThink || !string.Equals(filtered, input, StringComparison.Ordinal))
        {
            SawThinking = true;
        }

        if (!string.IsNullOrWhiteSpace(filtered))
        {
            SawVisibleContent = true;
        }

        return filtered;
    }

    private void NoteThinkProgress(string text)
    {
        var wasInside = _insideThink;
        NoteThinkTags(text);
        if (_openedThinkTag)
        {
            if (!_insideThink && !string.IsNullOrWhiteSpace(text))
            {
                if (wasInside)
                {
                    if (!string.IsNullOrWhiteSpace(TextAfterLastClose(text)))
                    {
                        SawVisibleContent = true;
                    }
                }
                else
                {
                    SawVisibleContent = true;
                }
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            _insideThink = false;
            SawVisibleContent = true;
        }
    }

    private void NoteThinkTags(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (!_insideThink)
            {
                var open = IndexOfAnyTag(text, i, OpenTags, out var openTag);
                if (open < 0)
                {
                    break;
                }

                _insideThink = true;
                _openedThinkTag = true;
                SawThinking = true;
                i = open + openTag.Length;
                continue;
            }

            var close = IndexOfAnyTag(text, i, CloseTags, out var closeTag);
            if (close < 0)
            {
                break;
            }

            _insideThink = false;
            i = close + closeTag.Length;
        }
    }

    private static string TextAfterLastClose(string text)
    {
        var last = -1;
        var tagLength = 0;
        foreach (var tag in CloseTags)
        {
            var idx = text.LastIndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (idx > last)
            {
                last = idx;
                tagLength = tag.Length;
            }
        }

        return last < 0 ? string.Empty : text[(last + tagLength)..];
    }

    private static int IndexOfAnyTag(string input, int start, string[] tags, out string matched)
    {
        matched = string.Empty;
        var best = -1;
        foreach (var tag in tags)
        {
            var idx = input.IndexOf(tag, start, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                continue;
            }

            if (best < 0 || idx < best)
            {
                best = idx;
                matched = tag;
            }
        }

        return best;
    }

    private static int HoldPartialTagPrefix(string input, int start, string[] tags)
    {
        var tail = input.AsSpan(start);
        var hold = 0;
        foreach (var tag in tags)
        {
            for (var len = 1; len < tag.Length && len <= tail.Length; len++)
            {
                if (tail[^len..].Equals(tag.AsSpan(0, len), StringComparison.OrdinalIgnoreCase))
                {
                    hold = Math.Max(hold, len);
                }
            }
        }

        return hold;
    }
}
