using System;
using System.IO;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services.ContextEngine;

/// <summary>
/// Re-asserts the durable essence after a compaction edge or cold resume, so the essence survives
/// even though the raw context was discarded. Two delivery channels (endpoint-agnostic source,
/// branch by endpoint):
///   * Cline — rewrite <c>&lt;sessionId&gt;.compaction.json</c> in Cline's rich format (the
///     essence as the compaction_summary + a recent-message tail), so Cline assimilates it on its
///     next turn.
///   * Local — build the essence re-injection block for the gateway to prepend to the system
///     prompt for this turn (the next turn carries it forward; no per-turn re-add).
/// Never throws: a re-assert failure must never break a turn.
/// </summary>
public sealed class EssenceReasserter
{
    private readonly EssenceLog _essence;

    public EssenceReasserter(EssenceLog essence)
    {
        _essence = essence ?? throw new ArgumentNullException(nameof(essence));
    }

    /// <summary>
    /// Rewrites the Cline compaction file for <paramref name="sessionId"/> with the current
    /// essence (as the compaction_summary) plus a recent-message tail read from the session's
    /// .messages.json. Returns the written path, or null on failure / no essence.
    /// </summary>
    public string? ReassertClineCompaction(string sessionId, string? messagesPath)
    {
        var essence = _essence.Load();
        if (!essence.HasContent())
        {
            return null; // nothing to re-assert
        }

        var summary = essence.ToClineSummary();
        var (tail, sourceCount, lastKey) = ReadRecentTail(messagesPath, maxMessages: 12);

        // Build the rich compaction file (summary + recent tail) in Cline's native shape.
        return ClineCompactionWriter.WriteCompactionFileRich(
            sessionId,
            summary,
            sourceCount,
            lastKey,
            tail);
    }

    /// <summary>
    /// Builds the essence re-injection block for a local endpoint (to prepend to the system
    /// prompt for this turn). Returns an empty string when there is no essence.
    /// </summary>
    public string BuildLocalInjectionBlock()
    {
        var essence = _essence.Load();
        if (!essence.HasContent())
        {
            return string.Empty;
        }

        return essence.ToReinjectionBlock();
    }

    /// <summary>
    /// Reads the most recent N messages from a Cline .messages.json as a JsonArray (the
    /// "recent tail" that follows the compaction_summary in Cline's native format). Also returns
    /// the total source message count and the last message key (for the compaction header).
    /// Never throws; returns an empty array on any failure.
    /// </summary>
    private static (JsonArray tail, int sourceCount, string lastKey) ReadRecentTail(string? messagesPath, int maxMessages)
    {
        var empty = (new JsonArray(), 0, string.Empty);
        if (string.IsNullOrWhiteSpace(messagesPath) || !File.Exists(messagesPath))
        {
            return empty;
        }

        try
        {
            var text = File.ReadAllText(messagesPath);
            var doc = JsonNode.Parse(text) as JsonObject;
            if (doc?["messages"] is not JsonArray messages)
            {
                return empty;
            }

            var count = messages.Count;
            var lastKey = string.Empty;
            if (count > 0 && messages[count - 1] is JsonObject lastMsg)
            {
                lastKey = lastMsg["id"]?.ToString() ?? string.Empty;
                if (!string.IsNullOrEmpty(lastKey))
                {
                    lastKey = "id:" + lastKey;
                }
            }

            var tail = new JsonArray();
            var start = Math.Max(0, count - maxMessages);
            for (var i = start; i < count; i++)
            {
                // CLONE each message before adding it to the tail array. A JsonNode can only have
                // one parent at a time; messages[i] already belongs to the `messages` array, so
                // adding it directly would throw InvalidOperationException ("The node already has
                // a parent") and silently drop the entire tail. Cloning detaches it safely.
                tail.Add(messages[i]?.DeepClone());
            }

            return (tail, count, lastKey);
        }
        catch
        {
            return empty;
        }
    }
}