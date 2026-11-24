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
///
/// Native streamed tool calls are HELD until the call is complete (the choice's finish_reason,
/// or [DONE]) and then sent as one piece. A streamed call arrives in fragments — the first
/// carries the id and name with empty arguments, later ones carry only argument text — and the
/// outgoing-write filter (<see cref="LocalSessionArtifactPolicy.FilterMessageToolCalls"/>) used
/// to judge each fragment alone: "write, no path" was dropped, the nameless argument fragments
/// were not, and the Client app assembled a call with no name and broken arguments. Harness
/// failed the turn on it ("Tool call \"\": Unexpected non-whitespace character after JSON");
/// seen 24 Nov on almost every Harness turn that wrote a file. The filter now sees whole calls.
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

    /// <summary>Native streamed tool calls being assembled, by their stream index.</summary>
    private readonly SortedDictionary<int, JsonObject> _assembling = new();

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
        if (_assembling.Count > 0)
        {
            // The stream ended without a finish_reason on the choice: send what was assembled.
            var flush = new JsonObject();
            var finish = FlushAssembled(flush);
            _holding = false;
            return ["data: " + new JsonObject
            {
                ["id"] = "chatcmpl-fluxmux-tool-calls",
                ["object"] = "chat.completion.chunk",
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = "local",
                ["choices"] = new JsonArray
                {
                    new JsonObject { ["index"] = 0, ["delta"] = flush, ["finish_reason"] = finish },
                },
            }.ToJsonString()];
        }

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
            var finishNow = choice["finish_reason"]?.ToString();
            if (delta is not null && delta["tool_calls"] is JsonArray { Count: > 0 } streamedCalls)
            {
                _nativeToolCalls = true;
                if (_holding)
                {
                    _held.Clear();
                    _holding = false;
                }

                foreach (var fragment in streamedCalls.OfType<JsonObject>())
                {
                    MergeFragment(fragment);
                }

                delta.Remove("tool_calls");
            }

            if (_assembling.Count > 0)
            {
                if (!string.IsNullOrEmpty(finishNow))
                {
                    if (delta is null)
                    {
                        delta = new JsonObject();
                        choice["delta"] = delta;
                    }

                    choice["finish_reason"] = FlushAssembled(delta);
                    continue;
                }

                // Still assembling: forward the chunk only if it carries something else.
                if (!HasOtherDeltaFields(delta))
                {
                    forward = false;
                }

                continue;
            }

            if (delta is not null && HasStructuredCalls(delta))
            {
                // Legacy single function_call: passed through as before.
                _nativeToolCalls = true;
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

    /// <summary>Adds one streamed fragment to the call with the same index.</summary>
    private void MergeFragment(JsonObject fragment)
    {
        var index = fragment["index"] is JsonValue v && v.TryGetValue<int>(out var i) ? i : _assembling.Count;
        if (!_assembling.TryGetValue(index, out var call))
        {
            call = new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = "", ["arguments"] = "" } };
            _assembling[index] = call;
        }

        if (fragment["id"]?.ToString() is { Length: > 0 } id)
        {
            call["id"] = id;
        }

        if (fragment["type"]?.ToString() is { Length: > 0 } type)
        {
            call["type"] = type;
        }

        if (fragment["function"] is JsonObject function)
        {
            var target = (JsonObject)call["function"]!;
            if (function["name"]?.ToString() is { Length: > 0 } name)
            {
                target["name"] = (target["name"]?.ToString() ?? string.Empty) + name;
            }

            if (function["arguments"]?.ToString() is { Length: > 0 } args)
            {
                target["arguments"] = (target["arguments"]?.ToString() ?? string.Empty) + args;
            }
        }
    }

    /// <summary>
    /// Puts the assembled calls (whole, filtered) onto <paramref name="delta"/> and returns the
    /// finish_reason to send: tool_calls when any call is left, stop when all were dropped.
    /// </summary>
    private string FlushAssembled(JsonObject delta)
    {
        var calls = new JsonArray();
        foreach (var call in _assembling.Values)
        {
            calls.Add(call);
        }

        _assembling.Clear();
        var message = new JsonObject { ["tool_calls"] = calls };
        if (ReadDeltaContent(delta).Length > 0)
        {
            message["content"] = ReadDeltaContent(delta);
        }

        if (LocalSessionArtifactPolicy.FilterMessageToolCalls(message, _artifacts) > 0)
        {
            BlockedWrites = true;
        }

        if (message["tool_calls"] is JsonArray { Count: > 0 } kept)
        {
            for (var i = 0; i < kept.Count; i++)
            {
                if (kept[i] is JsonObject call)
                {
                    call["index"] = i;
                }
            }

            delta["tool_calls"] = kept.DeepClone();
            return "tool_calls";
        }

        if (message["content"]?.ToString() is { Length: > 0 } note && ReadDeltaContent(delta).Length == 0)
        {
            delta["content"] = note;
        }

        return "stop";
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
