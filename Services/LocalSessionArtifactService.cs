using System;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// The gateway-side context-window / auto-compact policy for local-model sessions.
///
/// Local models have a hard context window. When the prompt the gateway is about to
/// forward grows past a threshold fraction of that window, the gateway asks the client
/// to compact the session (summarise the older turns) so the next request fits.
///
/// This type owns the *decision* (should we compact, and why). It is deliberately
/// stateless and cheap: it counts prompt tokens with a character heuristic and
/// compares against the configured threshold. The actual compaction (rewriting the
/// message list) is done by the client; this service only tells the gateway when.
///
/// It pairs with <see cref="LocalSessionArtifactPolicy"/>, which trims bulky tool
/// results and diagnostic artifacts from the same payload.
/// </summary>
public sealed class LocalSessionArtifactService
{
    /// <summary>
    /// Default auto-compact threshold: compact when the prompt reaches 80% of the
    /// context window. Leaves headroom for the model's reply so the request does not
    /// overflow mid-generation.
    /// </summary>
    public const double DefaultAutoCompactThreshold = 0.80;

    /// <summary>
    /// Target fraction of the window the prompt should be back under after a compact.
    /// Used to verify a compact actually reclaimed enough context.
    /// </summary>
    public const double DefaultTargetAfterCompact = 0.50;

    /// <summary>
    /// The context window (in tokens) the local model was launched with.
    /// </summary>
    public int ContextWindow { get; }

    /// <summary>
    /// Fraction of the window at which auto-compact fires (default 0.80).
    /// </summary>
    public double AutoCompactThreshold { get; }

    /// <summary>
    /// Fraction of the window the prompt should be back under after a compact
    /// (default 0.50).
    /// </summary>
    public double TargetAfterCompact { get; }

    /// <summary>
    /// Creates the service for a model with the given context window, using the
    /// default 80% auto-compact threshold and 50% post-compact target.
    /// </summary>
    public LocalSessionArtifactService(int contextWindow)
        : this(contextWindow, DefaultAutoCompactThreshold, DefaultTargetAfterCompact)
    {
    }

    /// <summary>
    /// Creates the service with an explicit threshold and post-compact target.
    /// </summary>
    public LocalSessionArtifactService(int contextWindow, double autoCompactThreshold, double targetAfterCompact)
    {
        ContextWindow = contextWindow;
        AutoCompactThreshold = autoCompactThreshold;
        TargetAfterCompact = targetAfterCompact;
    }

    /// <summary>
    /// Decides whether the gateway should ask the client to compact before forwarding
    /// <paramref name="prompt"/>.
    /// </summary>
    /// <param name="prompt">The outgoing payload (a JSON object with a "messages" array).</param>
    /// <param name="messages">The message array inside the prompt (used for the reason text).</param>
    public LocalSessionArtifactDecision Evaluate(JsonObject prompt, JsonArray? messages)
    {
        var tokens = LocalSessionArtifactPolicy.CountPromptTokens(prompt);
        var window = ContextWindow;

        if (window <= 0)
        {
            // No known window: never auto-compact (we cannot judge overflow).
            return new LocalSessionArtifactDecision(
                ShouldAutoCompact: false,
                Tokens: tokens,
                Window: window,
                Threshold: AutoCompactThreshold,
                Target: TargetAfterCompact,
                Reason: "context window unknown; auto-compact disabled");
        }

        var fraction = (double)tokens / window;
        if (fraction >= AutoCompactThreshold)
        {
            var messageCount = messages?.Count ?? 0;
            return new LocalSessionArtifactDecision(
                ShouldAutoCompact: true,
                Tokens: tokens,
                Window: window,
                Threshold: AutoCompactThreshold,
                Target: TargetAfterCompact,
                Reason: $"prompt {tokens} tokens is {fraction:P0} of the {window}-token window " +
                        $"(>= {AutoCompactThreshold:P0} threshold); compact {messageCount} messages to ~{TargetAfterCompact:P0}");
        }

        return new LocalSessionArtifactDecision(
            ShouldAutoCompact: false,
            Tokens: tokens,
            Window: window,
            Threshold: AutoCompactThreshold,
            Target: TargetAfterCompact,
            Reason: $"prompt {tokens} tokens is {fraction:P0} of the {window}-token window (< {AutoCompactThreshold:P0} threshold)");
    }
}

/// <summary>
/// The result of <see cref="LocalSessionArtifactService.Evaluate"/>.
/// </summary>
public sealed record LocalSessionArtifactDecision
(
    /// <summary>Whether the gateway should ask the client to compact the session.</summary>
    bool ShouldAutoCompact,

    /// <summary>Estimated prompt token count.</summary>
    int Tokens,

    /// <summary>The context window in tokens (0 if unknown).</summary>
    int Window,

    /// <summary>The auto-compact threshold fraction that was applied.</summary>
    double Threshold,

    /// <summary>The post-compact target fraction.</summary>
    double Target,

    /// <summary>Human-readable explanation of the decision (for logs / test output).</summary>
    string? Reason = null
);