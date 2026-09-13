using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Holds suspected tool XML across SSE deltas, then emits structured
/// <c>tool_calls</c> when the Client app declared those names. Prose before
/// the markup is forwarded immediately. Native grammar-mode calls go through
/// untouched and cancel a held XML buffer (it was the same call in text form).
/// </summary>
public sealed class LocalToolCallHealSession
{
    private readonly HashSet<string> _declared;
    private readonly bool _allowParallel;
    private readonly LocalSessionArtifacts? _artifacts;
    private readonly StringBuilder _held = new();
    private bool _holding;
    private bool _nativeToolCalls;
    private bool _emittedFinish;

    public LocalToolCallHealSession(
        IReadOnlyCollection<string> declaredTools,
        bool allowParallel = true,
        LocalSessionArtifacts? artifacts = null)
    {
        _declared = new HashSet<string>(declaredTools, StringComparer.OrdinalIgnoreCase);
        _allowParallel = allowParallel;
        _artifacts = artifacts;
    }

    public bool Healed { get; private set; }

    public bool BlockedWrites { get; private set; }

    public readonly record struct LineDecision(bool Forward, string Line, IReadOnlyList<string> ExtraBefore);

    public LineDecision ApplySseLine(string line)
    {
        if (_declared.Count == 0
            || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return new LineDecision(true, line, Array.Empty<string>());
        }

        var payload = line.Substring(5).TrimStart();
        if (payload.Length == 0)
        {
            return new LineDecision(true, line, Array.Empty<string>());
        }

        if (payload.Equals("[DONE]", StringComparison.Ordinal))
        {
            var extras = TakeFinishChunks();
            return new LineDecision(true, line, extras);
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(payload) as JsonObject;
        }
        catch
        {
            return new LineDecision(true, line, Array.Empty<string>());
        }

        if (root is null)
        {
            return new LineDecision(true, line, Array.Empty<string>());
        }

        if (root["choices"] is not JsonArray { Count: > 0 })
        {
            return new LineDecision(true, line, Array.Empty<string>());
        }

        if (HasCompleteMessage(root))
        {
            if (LocalToolCallHealing.HealCompletion(root, _declared, _allowParallel, _artifacts))
            {
                Healed = true;
            }

            if (LocalSessionArtifactPolicy.FilterCompletion(root, _artifacts) > 0)
            {
                BlockedWrites = true;
            }

            return new LineDecision(true, "data: " + root.ToJsonString(), Array.Empty<string>());
        }

        var extrasBefore = Array.Empty<string>();
        var forward = ApplyDelta(root, ref extrasBefore);
        if (!forward)
        {
            return new LineDecision(false, line, extrasBefore);
        }

        return new LineDecision(true, "data: " + root.ToJsonString(), extrasBefore);
    }

    public IReadOnlyList<string> TakeFinishChunks()
    {
        if (_emittedFinish || !_holding || _nativeToolCalls || _held.Length == 0)
        {
            _holding = false;
            return Array.Empty<string>();
        }

        _emittedFinish = true;
        _holding = false;
        var held = _held.ToString();
        _held.Clear();
        if (TryHealHeld(held, out var chunk))
        {
            Healed = true;
            return ["data: " + chunk.ToJsonString()];
        }

        if (string.IsNullOrWhiteSpace(held))
        {
            return Array.Empty<string>();
        }

        return ["data: " + ContentDelta(held).ToJsonString()];
    }

    private bool ApplyDelta(JsonObject root, ref string[] extrasBefore)
    {
        if (root["choices"] is not JsonArray choices)
        {
            return true;
        }

        var forward = true;
        foreach (var node in choices)
        {
            if (node is not JsonObject choice)
            {
                continue;
            }

            var delta = choice["delta"] as JsonObject;
            if (delta is not null && HasStructuredCalls(delta))
            {
                _nativeToolCalls = true;
                if (_holding)
                {
                    _held.Clear();
                    _holding = false;
                }

                if (LocalSessionArtifactPolicy.FilterMessageToolCalls(delta, _artifacts) > 0)
                {
                    BlockedWrites = true;
                }

                continue;
            }

            var content = ReadDeltaContent(delta);
            var finish = choice["finish_reason"]?.ToString();
            if (!string.IsNullOrEmpty(content))
            {
                if (_nativeToolCalls)
                {
                    continue;
                }

                if (_holding)
                {
                    _held.Append(content);
                    ClearContent(delta);
                    forward = HasOtherDeltaFields(delta) || !string.IsNullOrEmpty(finish);
                    continue;
                }

                var markupAt = LocalToolCallHealing.IndexOfVisibleToolMarkup(content);
                if (markupAt < 0)
                {
                    continue;
                }

                var prefix = content[..markupAt];
                _held.Append(content[markupAt..]);
                _holding = true;
                if (prefix.Length > 0)
                {
                    SetContent(delta, prefix);
                }
                else
                {
                    ClearContent(delta);
                    forward = HasOtherDeltaFields(delta) || !string.IsNullOrEmpty(finish);
                }
            }

            if (string.IsNullOrEmpty(finish) || _nativeToolCalls)
            {
                continue;
            }

            if (!_holding || _held.Length == 0)
            {
                continue;
            }

            var held = _held.ToString();
            _held.Clear();
            _holding = false;
            _emittedFinish = true;
            if (TryHealHeld(held, out var healed, includeFinish: false))
            {
                Healed = true;
                if (delta is null)
                {
                    delta = new JsonObject();
                    choice["delta"] = delta;
                }

                MergeHealedDelta(delta, healed);
                choice["finish_reason"] = "tool_calls";
                forward = true;
            }
            else
            {
                extrasBefore = ["data: " + ContentDelta(held).ToJsonString()];
                forward = true;
            }
        }

        return forward;
    }

    private bool TryHealHeld(string held, out JsonObject chunk, bool includeFinish = true)
    {
        chunk = ContentDelta(string.Empty);
        var message = new JsonObject { ["content"] = held };
        if (!LocalToolCallHealing.HealMessage(message, _declared, _allowParallel, _artifacts))
        {
            return false;
        }

        if (message["tool_calls"] is not JsonArray { Count: > 0 } calls)
        {
            return false;
        }

        var leftover = message["content"]?.ToString();
        chunk = new JsonObject
        {
            ["id"] = "chatcmpl-fluxmux-tool-heal",
            ["object"] = "chat.completion.chunk",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = "local",
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = BuildHealedDelta(calls, leftover),
                    ["finish_reason"] = includeFinish ? "tool_calls" : JsonValue.Create((string?)null)
                }
            }
        };
        return true;
    }

    private static JsonObject BuildHealedDelta(JsonArray calls, string? leftover)
    {
        var delta = new JsonObject
        {
            ["tool_calls"] = calls.DeepClone()
        };
        if (!string.IsNullOrWhiteSpace(leftover))
        {
            delta["content"] = leftover;
        }

        return delta;
    }

    private static void MergeHealedDelta(JsonObject delta, JsonObject chunk)
    {
        var healed = chunk["choices"]?[0]?["delta"] as JsonObject;
        if (healed is null)
        {
            return;
        }

        if (healed["tool_calls"] is JsonNode calls)
        {
            delta["tool_calls"] = calls.DeepClone();
        }

        if (healed["content"] is JsonNode leftover)
        {
            delta["content"] = leftover.DeepClone();
        }
        else
        {
            ClearContent(delta);
        }
    }

    private static JsonObject ContentDelta(string content)
        => new()
        {
            ["id"] = "chatcmpl-fluxmux-tool-heal",
            ["object"] = "chat.completion.chunk",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = "local",
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject { ["content"] = content }
                }
            }
        };

    private static bool HasCompleteMessage(JsonObject root)
        => root["choices"] is JsonArray choices
           && choices.OfType<JsonObject>().Any(choice =>
               choice["message"] is JsonObject && choice["delta"] is null);

    private static bool HasStructuredCalls(JsonObject message)
        => message["tool_calls"] is JsonArray { Count: > 0 }
           || message["function_call"] is JsonObject;

    private static string ReadDeltaContent(JsonObject? delta)
        => delta?["content"]?.ToString() ?? string.Empty;

    private static void SetContent(JsonObject? delta, string content)
    {
        if (delta is null)
        {
            return;
        }

        delta["content"] = content;
    }

    private static void ClearContent(JsonObject? delta)
    {
        if (delta is null)
        {
            return;
        }

        if (delta.ContainsKey("content"))
        {
            delta["content"] = "";
        }
    }

    private static bool HasOtherDeltaFields(JsonObject? delta)
    {
        if (delta is null || delta.Count == 0)
        {
            return false;
        }

        foreach (var pair in delta)
        {
            if (pair.Key.Equals("content", StringComparison.OrdinalIgnoreCase))
            {
                var text = pair.Value?.ToString();
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                return true;
            }

            if (pair.Key.Equals("role", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(pair.Value?.ToString()))
            {
                continue;
            }

            return true;
        }

        return false;
    }
}
