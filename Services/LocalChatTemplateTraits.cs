using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// What the loaded local model's own chat template says about thinking, read from llama-server's
/// <c>/props</c> (28 Nov). FluxMux keeps raw thinking in content (<c>--reasoning-format none</c>)
/// and its filters know <c>&lt;think&gt;</c>-style tags; Inkling 2 Mini wraps thinking as
/// <c>"&lt;|content_thinking|&gt;" ~ reasoning_content ~ "&lt;|end_message|&gt;"</c>, so its
/// thinking and control tokens reached Cline as the answer. The markers are taken from the
/// template's own rendering of <c>reasoning_content</c> and turned into <c>&lt;think&gt;</c> /
/// <c>&lt;/think&gt;</c>; other <c>&lt;|…|&gt;</c> control tokens the template writes are dropped
/// from replies. Its template also never reads <c>enable_thinking</c>, so Reasoning Off sends the
/// lowest <c>reasoning_effort</c> level llama-server reports for the template.
/// </summary>
public sealed record LocalChatTemplateTraits(
    string? ThinkOpen,
    string? ThinkClose,
    IReadOnlyList<string> ControlTokens,
    bool ReadsEnableThinking,
    IReadOnlyList<string> EffortLevels)
{
    public static readonly LocalChatTemplateTraits None = new(null, null, Array.Empty<string>(), true, Array.Empty<string>());

    /// <summary>True when the template marks thinking with something other than &lt;think&gt;-style tags.</summary>
    public bool TranslatesThinking
        => !string.IsNullOrEmpty(ThinkOpen) && !string.IsNullOrEmpty(ThinkClose)
           && !ThinkOpen.Contains("<think", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The effort level Reasoning Off sends: the lowest the template accepts, when the template
    /// cannot switch thinking off through enable_thinking; otherwise null.
    /// </summary>
    public string? OffEffort => !ReadsEnableThinking && EffortLevels.Count > 0 ? EffortLevels[0] : null;

    private static readonly string[] EffortScale = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    /// <summary>
    /// The template level nearest <paramref name="effort"/> on the usual none…max scale, so a level
    /// the template does not list (Inkling reports up to "max", not "xhigh") is never sent to a
    /// template that rejects unknown levels; <paramref name="effort"/> itself when nothing is known.
    /// </summary>
    public string NearestEffort(string effort)
    {
        if (EffortLevels.Count == 0 || EffortLevels.Contains(effort, StringComparer.OrdinalIgnoreCase))
        {
            return effort;
        }

        var wanted = Array.FindIndex(EffortScale, level => level.Equals(effort, StringComparison.OrdinalIgnoreCase));
        if (wanted < 0)
        {
            return effort;
        }

        return EffortLevels
            .Select(level => (Level: level, Index: Array.FindIndex(EffortScale, known => known.Equals(level, StringComparison.OrdinalIgnoreCase))))
            .Where(pair => pair.Index >= 0)
            .OrderBy(pair => Math.Abs(pair.Index - wanted))
            .ThenByDescending(pair => pair.Index)
            .Select(pair => pair.Level)
            .DefaultIfEmpty(effort)
            .First();
    }

    // "<open>" ~ message.reasoning_content ~ "<close>" (Jinja ~ or +, optional filters/strip()).
    private static readonly Regex ReasoningWrap = new(
        @"([""'])((?:(?!\1).){1,80})\1\s*(?:~|\+)\s*(?:message\.)?reasoning_content(?:\s*\|\s*\w+|\.\w+\([^)]*\))*\s*(?:~|\+)\s*([""'])((?:(?!\3).){1,80})\3",
        RegexOptions.Compiled);

    private static readonly Regex ControlToken = new(@"<\|[A-Za-z0-9_]{1,40}\|>", RegexOptions.Compiled);

    public static LocalChatTemplateTraits FromProps(JsonObject? props)
    {
        var template = props?["chat_template"]?.ToString() ?? string.Empty;
        if (template.Length == 0)
        {
            return None;
        }

        string? open = null, close = null;
        var wrap = ReasoningWrap.Match(template);
        if (wrap.Success)
        {
            open = Unescape(wrap.Groups[2].Value).Trim();
            close = Unescape(wrap.Groups[4].Value).Trim();
        }

        var tokens = ControlToken.Matches(template).Select(m => m.Value)
            .Where(t => t != open && t != close)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var levels = new List<string>();
        if (props?["chat_template_caps"] is JsonObject caps
            && caps["supports_reasoning_effort"]?.GetValue<bool>() == true
            && caps["reasoning_effort_levels"] is JsonArray list)
        {
            levels.AddRange(list.Select(n => n?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s))!);
        }

        return new LocalChatTemplateTraits(open, close, tokens, template.Contains("enable_thinking", StringComparison.Ordinal), levels);
    }

    private static string Unescape(string s) => s.Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\t", "\t", StringComparison.Ordinal);

    private static volatile LocalChatTemplateTraits _current = None;
    private static string _currentKey = string.Empty;
    private static DateTime _lastFailureUtc = DateTime.MinValue;
    private static readonly SemaphoreSlim RefreshLock = new(1, 1);

    /// <summary>The traits of the local model the gateway is serving now.</summary>
    public static LocalChatTemplateTraits Current => _current;

    /// <summary>
    /// Reads <c>/props</c> from the llama-server on <paramref name="port"/> once per loaded model
    /// (<paramref name="modelKey"/>: port, model and profile). Best-effort: on failure the traits are
    /// None and it is tried again after 30 s.
    /// </summary>
    public static async Task EnsureAsync(HttpClient http, int port, string modelKey, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        if (port <= 0 || (modelKey == _currentKey && (!ReferenceEquals(_current, None) || DateTime.UtcNow - _lastFailureUtc < TimeSpan.FromSeconds(30))))
        {
            return;
        }

        await RefreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (modelKey == _currentKey && (!ReferenceEquals(_current, None) || DateTime.UtcNow - _lastFailureUtc < TimeSpan.FromSeconds(30)))
            {
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var text = await http.GetStringAsync($"http://127.0.0.1:{port}/props", timeout.Token).ConfigureAwait(false);
            var traits = FromProps(JsonNode.Parse(text) as JsonObject);
            _current = traits;
            _currentKey = modelKey;
            _lastFailureUtc = ReferenceEquals(traits, None) ? DateTime.UtcNow : DateTime.MinValue;
            log?.Invoke("local chat template: thinking " + (traits.TranslatesThinking ? traits.ThinkOpen + " … " + traits.ThinkClose + " shown as <think>" : "uses <think> tags or none")
                + (traits.OffEffort is { } off ? "; Reasoning Off sends reasoning_effort " + off + " (template ignores enable_thinking)" : string.Empty));
        }
        catch
        {
            _current = None;
            _currentKey = modelKey;
            _lastFailureUtc = DateTime.UtcNow;
        }
        finally
        {
            RefreshLock.Release();
        }
    }

    /// <summary>For tests and a model change: set the traits directly.</summary>
    internal static void SetCurrentForTests(LocalChatTemplateTraits traits) => _current = traits;

    /// <summary>
    /// Turns this model's thinking markers into &lt;think&gt; / &lt;/think&gt; and drops its other
    /// control tokens, across the deltas of one reply. llama-server sends whole tokens in each delta
    /// and these markers are single special tokens, so a marker is never split between deltas.
    /// </summary>
    public sealed class Translator
    {
        private readonly LocalChatTemplateTraits _traits;
        private bool _inside;

        public Translator(LocalChatTemplateTraits traits) => _traits = traits;

        public string Translate(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('<') < 0)
            {
                return text;
            }

            var output = new StringBuilder(text.Length + 8);
            var i = 0;
            while (i < text.Length)
            {
                if (text[i] == '<')
                {
                    if (!_inside && Matches(text, i, _traits.ThinkOpen))
                    {
                        output.Append("<think>");
                        _inside = true;
                        i += _traits.ThinkOpen!.Length;
                        continue;
                    }

                    if (Matches(text, i, _traits.ThinkClose))
                    {
                        if (_inside)
                        {
                            output.Append("</think>");
                            _inside = false;
                        }

                        i += _traits.ThinkClose!.Length;
                        continue;
                    }

                    var token = _traits.ControlTokens.FirstOrDefault(t => Matches(text, i, t));
                    if (token is not null)
                    {
                        i += token.Length;
                        continue;
                    }
                }

                output.Append(text[i]);
                i++;
            }

            return output.ToString();
        }

        private static bool Matches(string text, int index, string? marker)
            => !string.IsNullOrEmpty(marker) && string.CompareOrdinal(text, index, marker, 0, marker.Length) == 0;
    }

    /// <summary>Translates the content of every choice's message and delta in a whole (non-stream) reply.</summary>
    public bool TranslateCompletion(JsonObject root)
    {
        if (!TranslatesThinking || root["choices"] is not JsonArray choices)
        {
            return false;
        }

        var changed = false;
        foreach (var choice in choices.OfType<JsonObject>())
        {
            foreach (var key in new[] { "message", "delta" })
            {
                if (choice[key] is JsonObject message && TranslateContent(message, new Translator(this)))
                {
                    changed = true;
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// Translates the string content of assistant messages in a request: Client apps send back
    /// replies saved before FluxMux translated them, with this model's raw thinking markers in them.
    /// </summary>
    public bool TranslateHistory(JsonArray messages)
    {
        if (!TranslatesThinking)
        {
            return false;
        }

        var changed = false;
        foreach (var message in messages.OfType<JsonObject>())
        {
            if (message["role"]?.ToString() == "assistant" && TranslateContent(message, new Translator(this)))
            {
                changed = true;
            }
        }

        return changed;
    }

    private static bool TranslateContent(JsonObject message, Translator translator)
    {
        if (message["content"] is JsonValue value && value.TryGetValue<string>(out var content) && content.Length > 0)
        {
            var translated = translator.Translate(content);
            if (!string.Equals(translated, content, StringComparison.Ordinal))
            {
                message["content"] = translated;
                return true;
            }
        }

        return false;
    }
}
