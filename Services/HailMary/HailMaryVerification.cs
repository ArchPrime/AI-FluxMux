using System;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services.HailMary;

/// <summary>
/// Did the Client app take Hail Mary's rebuild? (27 Nov.) A rebuild is written into the app's own
/// files and the app picks it up — or not — when the chat is next opened; Cline, for one, applies
/// it only when it starts a session for the task and silently resends the whole chat otherwise
/// (a reopened chat that was still open, a manifest it rejects, a fingerprint that no longer
/// matches). Nothing on screen shows which: Cline still displays the full history either way.
/// So after a rebuild the check is armed, and the next request from that Client app for that chat
/// tells: it starts with AI-FluxMux's summary (<see cref="HandoverBrief.RescueSummaryHeading"/>),
/// or it is the full chat again. The chat is recognised by its opening line, which the summary
/// also quotes under "Where it started", so another chat from the same app is ignored. A request
/// without the summary is reported once; the check stays armed until the summary is seen or it
/// expires, so reopening the chat afterwards is still confirmed.
/// </summary>
public static class HailMaryVerification
{
    /// <summary>How long after a rebuild the check stays armed.</summary>
    public static readonly TimeSpan ArmedFor = TimeSpan.FromHours(6);

    private sealed record Pending(string ClientApp, string OpeningProbe, int MessagesBefore, DateTime ArmedUtc)
    {
        public bool ReportedNotTaken { get; set; }
    }

    private static readonly object Gate = new();
    private static Pending? _pending;

    /// <summary>Raised with a sentence for the person when the check has an answer.</summary>
    public static event Action<string>? Observed;

    /// <summary>Arms the check after a rebuild of <paramref name="clientApp"/>'s chat.</summary>
    public static void Arm(string? clientApp, string? openingProbe, int messagesBefore, DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(clientApp) || string.IsNullOrWhiteSpace(openingProbe))
        {
            return;
        }

        lock (Gate)
        {
            _pending = new Pending(clientApp.Trim(), openingProbe.Trim(), messagesBefore, nowUtc ?? DateTime.UtcNow);
        }
    }

    /// <summary>Forgets an armed check (tests, and a later rebuild replaces it anyway).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _pending = null;
        }
    }

    /// <summary>True while a check is armed.</summary>
    public static bool IsArmed
    {
        get
        {
            lock (Gate)
            {
                return _pending is not null;
            }
        }
    }

    /// <summary>
    /// Looks at a chat request from <paramref name="sender"/>; returns the sentence it raised, or
    /// null when this request says nothing about the rebuild (another app, another chat, nothing
    /// armed, or a "not taken" already reported).
    /// </summary>
    public static string? Observe(JsonArray? messages, string? sender, DateTime? nowUtc = null)
    {
        if (messages is null || string.IsNullOrWhiteSpace(sender))
        {
            return null;
        }

        string? sentence;
        lock (Gate)
        {
            var pending = _pending;
            if (pending is null)
            {
                return null;
            }

            if ((nowUtc ?? DateTime.UtcNow) - pending.ArmedUtc > ArmedFor)
            {
                _pending = null;
                return null;
            }

            if (!string.Equals(pending.ClientApp, sender.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var text = messages.ToJsonString();
            if (!text.Contains(pending.OpeningProbe, StringComparison.Ordinal)
                && !text.Contains(JsonEscaped(pending.OpeningProbe), StringComparison.Ordinal))
            {
                return null;
            }

            if (text.Contains(HandoverBrief.RescueSummaryHeading, StringComparison.Ordinal))
            {
                _pending = null;
                sentence = pending.ClientApp + " is using the rebuilt chat: its request starts with AI-FluxMux's summary ("
                           + messages.Count + " messages, where the stuck chat had " + pending.MessagesBefore + ").";
            }
            else if (!pending.ReportedNotTaken)
            {
                pending.ReportedNotTaken = true;
                sentence = pending.ClientApp + " sent the whole chat again (" + messages.Count + " messages), so it has not picked up the rebuild. "
                           + (string.Equals(pending.ClientApp, ClientAppDetector.Cline, StringComparison.OrdinalIgnoreCase)
                               ? "In Cline, open another task, then reopen this one from history, and send retry."
                               : "Close and reopen this chat in " + pending.ClientApp + ", then send retry.");
            }
            else
            {
                return null;
            }
        }

        Observed?.Invoke(sentence);
        return sentence;
    }

    private static string JsonEscaped(string text)
    {
        var json = JsonValue.Create(text).ToJsonString();
        return json.Length >= 2 ? json[1..^1] : json;
    }
}
