using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Writes a .compaction.json file that Cline reads on subsequent turns.
/// This allows us to "resurrect" Cline by pre-compacting the context when Cline is unresponsive,
/// so that when Cline comes back, it thinks it has already compacted and doesn't reinflate.
/// </summary>
public static class ClineCompactionWriter
{
    /// <summary>
    /// Reads the raw message array from a Cline .messages.json file as JsonNodes (so they can be
    /// re-serialized byte-identically for hashing), or null on any failure.
    /// </summary>
    public static JsonArray? ReadMessagesArray(string messagesPath)
    {
        if (string.IsNullOrWhiteSpace(messagesPath) || !File.Exists(messagesPath))
        {
            return null;
        }

        // Retry a few times in case Cline is writing to the file at the same time.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(messagesPath));
                var arr = root?["messages"] as JsonArray;
                if (arr is not null && arr.Count > 0)
                {
                    return arr;
                }
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ClineCompaction] ReadMessagesArray attempt " + (attempt + 1) + " failed: " + ex.Message);
                if (attempt < 2)
                {
                    System.Threading.Thread.Sleep(200);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Computes the Cline source-prefix hash exactly as Cline does (function <c>aL</c> in
    /// @cline/core). Cline validates a .compaction.json at session load by recomputing this hash
    /// over the first <c>source_message_count</c> of its current messages and comparing it to
    /// <c>source_prefix_hash</c>. If they don't match, Cline silently discards the compaction.
    /// </summary>
    public static string ComputeClineSourcePrefixHash(JsonArray messages, int count)
    {
        using var sha256 = SHA256.Create();
        var utf8 = Encoding.UTF8;
        void Write(string s)
        {
            var bytes = utf8.GetBytes(s);
            sha256.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        // Use UnsafeRelaxedJsonEscaping (closest to JS) + MatchJsEscaping post-processing to
        // match JavaScript's JSON.stringify byte-for-byte (lowercase \uXXXX, raw astral chars).
        var opts = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        Write("cline-session-compaction-source-v2\n");
        Write(count + "\n");

        for (int i = 0; i < count && i < messages.Count; i++)
        {
            var msg = messages[i] as JsonObject;
            if (msg is null)
            {
                continue;
            }

            var role = msg["role"]?.GetValue<string>() ?? string.Empty;
            var content = NormalizeContentForHash(msg["content"], role);
            var sb = new StringBuilder();
            sb.Append('[');
            // b8 produces 7 [key, value] pairs in fixed order.
            sb.Append('[').Append('"').Append("role").Append('"').Append(',').Append('"').Append(role).Append('"').Append(']');
            sb.Append(",[\"content\",").Append(content.ToJsonString(opts)).Append(']');
            sb.Append(",[\"agent\",").Append(msg["agent"]?.ToJsonString(opts) ?? "null").Append(']');
            sb.Append(",[\"sessionId\",").Append(msg["sessionId"]?.ToJsonString(opts) ?? "null").Append(']');
            sb.Append(",[\"metadata\",").Append(msg["metadata"]?.ToJsonString(opts) ?? "null").Append(']');
            sb.Append(",[\"modelInfo\",").Append(msg["modelInfo"]?.ToJsonString(opts) ?? "null").Append(']');
            sb.Append(",[\"metrics\",").Append(msg["metrics"]?.ToJsonString(opts) ?? "null").Append(']');
            sb.Append(']');

            Write(MatchJsEscaping(sb.ToString()));
            Write("\n");
        }

        // Finalize the incremental hash before reading the digest.
        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var hash = sha256.Hash ?? Array.Empty<byte>();
        var hex = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            hex.Append(b.ToString("x2"));
        }

        return "sha256:" + hex.ToString();
    }

    /// <summary>
    /// Converts C#'s JSON escaping to match JavaScript's JSON.stringify byte-for-byte:
    ///  1. Lowercase the hex digits in \uXXXX escapes (JS uses lowercase).
    ///  2. Convert \uHHHH\uLLLL surrogate pairs back to the actual Unicode character
    ///     (JS emits astral characters as raw UTF-8, not surrogate pairs).
    /// Verified byte-identical against Cline's @cline/core aL() hash on real session data.
    /// </summary>
    private static string MatchJsEscaping(string json)
    {
        var sb = new StringBuilder(json.Length);
        for (int i = 0; i < json.Length; i++)
        {
            if (json[i] == '\\' && i + 5 < json.Length && json[i + 1] == 'u')
            {
                // Look ahead for a surrogate pair: \uHHHH\uLLLL
                if (i + 11 < json.Length && json[i + 6] == '\\' && json[i + 7] == 'u')
                {
                    var high = json.Substring(i + 2, 4);
                    var low = json.Substring(i + 8, 4);
                    if (ushort.TryParse(high, System.Globalization.NumberStyles.HexNumber, null, out var hi) &&
                        ushort.TryParse(low, System.Globalization.NumberStyles.HexNumber, null, out var lo) &&
                        char.IsHighSurrogate((char)hi) && char.IsLowSurrogate((char)lo))
                    {
                        sb.Append(char.ConvertFromUtf32(char.ConvertToUtf32((char)hi, (char)lo)));
                        i += 11;
                        continue;
                    }
                }

                // Single \uXXXX escape: lowercase the hex digits to match JS.
                sb.Append('\\');
                sb.Append('u');
                sb.Append(json.Substring(i + 2, 4).ToLowerInvariant());
                i += 5;
                continue;
            }
            sb.Append(json[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Normalizes a message's content the way Cline's <c>iL</c>/<c>de</c> do before hashing:
    /// for user messages, trim whitespace on string content or on the "text" parts of array
    /// content. Non-user messages pass through unchanged.
    /// </summary>
    private static JsonNode NormalizeContentForHash(JsonNode? content, string role)
    {
        if (content is null)
        {
            return JsonValue.Create(string.Empty);
        }

        if (role != "user")
        {
            return content.DeepClone();
        }

        if (content is JsonValue stringVal && stringVal.TryGetValue<string>(out var s))
        {
            return JsonValue.Create(s.Trim());
        }

        if (content is JsonArray arr)
        {
            var newArr = new JsonArray();
            foreach (var part in arr)
            {
                if (part is JsonObject po &&
                    po["type"]?.GetValue<string>() == "text" &&
                    po["text"] is JsonValue tv && tv.TryGetValue<string>(out var text))
                {
                    var clone = po.DeepClone() as JsonObject;
                    clone?["text"] = text.Trim();
                    newArr.Add(clone);
                }
                else
                {
                    newArr.Add(part.DeepClone());
                }
            }

            return newArr;
        }

        return content.DeepClone();
    }

    /// <summary>
    /// Computes the Cline message key (<c>oL</c>): "id:&lt;id&gt;" if it has a non-empty id,
    /// else "ts:&lt;role&gt;:&lt;ts&gt;", else "content:&lt;role&gt;:&lt;json&gt;".
    /// </summary>
    public static string ComputeClineMessageKey(JsonObject msg)
    {
        var id = msg["id"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(id))
        {
            return "id:" + id.Trim();
        }

        var role = msg["role"]?.GetValue<string>() ?? string.Empty;
        if (msg["ts"] is JsonValue tsVal && tsVal.TryGetValue<long>(out var ts))
        {
            return "ts:" + role + ":" + ts;
        }

        return "content:" + role + ":" + (msg["content"]?.ToJsonString() ?? "null");
    }

    /// <summary>
    /// Writes a .compaction.json file for the given Cline session.
    /// </summary>
    /// <summary>
    /// Writes a .compaction.json that Cline will actually ACCEPT at session load. Computes the
    /// correct <c>source_prefix_hash</c> from the real .messages.json and omits
    /// <c>conversation_id</c> (Cline's guard rejects a mismatched conversation_id, and most
    /// sessions have a null one). <paramref name="sourceMessageCount"/> is how many leading
    /// messages are being compacted away (typically the full count).
    /// </summary>
    public static string? WriteCompactionFileValid(
        string sessionId,
        string summary,
        JsonArray sourceMessages,
        int sourceMessageCount)
    {
        try
        {
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var sessionDir = Path.Combine(homeDir, ".cline", "data", "sessions", sessionId);
            var compactionPath = Path.Combine(sessionDir, $"{sessionId}.compaction.json");
            Directory.CreateDirectory(sessionDir);

            var clampedCount = Math.Max(1, Math.Min(sourceMessageCount, sourceMessages.Count));
            var sourcePrefixHash = ComputeClineSourcePrefixHash(sourceMessages, clampedCount);
            var lastMsg = sourceMessages[clampedCount - 1] as JsonObject;
            var sourceLastMessageKey = lastMsg is not null ? ComputeClineMessageKey(lastMsg) : string.Empty;

            var compaction = new JsonObject
            {
                ["version"] = 1,
                ["updated_at"] = DateTime.UtcNow.ToString("o"),
                // conversation_id intentionally omitted (see method docs).
                ["source_message_count"] = clampedCount,
                ["source_prefix_hash"] = sourcePrefixHash,
                ["source_last_message_key"] = sourceLastMessageKey,
                ["messages"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = "Context summary:\n\n" + summary,
                            },
                        },
                        ["metadata"] = new JsonObject
                        {
                            ["kind"] = "compaction_summary",
                            ["displayRole"] = "system",
                            ["userRunSpan"] = 1,
                            ["summary"] = summary,
                        },
                    },
                },
            };

            var json = compaction.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            });

            File.WriteAllText(compactionPath, json);
            System.Diagnostics.Debug.WriteLine("[ClineCompaction] Wrote valid compaction file: " + compactionPath);
            return compactionPath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[ClineCompaction] Failed to write valid compaction file: " + ex.Message);
            return null;
        }
    }

    /// <param name="sessionId">The Cline session ID (e.g., "1792478967620_pcp87").</param>
    /// <param name="summary">The compacted summary to include in the first message.</param>
    /// <param name="sourceMessageCount">The number of messages in the original conversation.</param>
    /// <param name="sourceLastMessageKey">The last message ID in the original conversation.</param>
    /// <returns>The path to the written .compaction.json file, or null if it failed.</returns>
    public static string? WriteCompactionFile(string sessionId, string summary, int sourceMessageCount, string sourceLastMessageKey)
    {
        try
        {
            // The .compaction.json file is stored in:
            // C:\Users\<username>\.cline\data\sessions\<sessionId>\<sessionId>.compaction.json
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var sessionDir = Path.Combine(homeDir, ".cline", "data", "sessions", sessionId);
            var compactionPath = Path.Combine(sessionDir, $"{sessionId}.compaction.json");

            // Create the session directory if it doesn't exist
            Directory.CreateDirectory(sessionDir);

            // Compute a hash of the summary (Cline uses this to verify the compaction is valid)
            var sourcePrefixHash = ComputeSha256Hash(summary);

            // Build the compaction JSON
            var compaction = new
            {
                version = 1,
                updated_at = DateTime.UtcNow.ToString("o"),
                conversation_id = sessionId,
                source_message_count = sourceMessageCount,
                source_prefix_hash = $"sha256:{sourcePrefixHash}",
                source_last_message_key = sourceLastMessageKey,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new[]
                        {
                            new
                            {
                                type = "text",
                                text = $"Context summary:\n\n{summary}"
                            }
                        },
                        metadata = new
                        {
                            kind = "compaction_summary",
                            displayRole = "system",
                            userRunSpan = 1,
                            summary = summary
                        }
                    }
                }
            };

            // Write the JSON file
            var json = JsonSerializer.Serialize(compaction, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });

            File.WriteAllText(compactionPath, json);

            System.Diagnostics.Debug.WriteLine($"[ClineCompaction] Wrote compaction file: {compactionPath}");
            return compactionPath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClineCompaction] Failed to write compaction file: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Writes a rich .compaction.json matching Cline's native format: the compaction_summary as
    /// the first message, followed by a recent-message tail (so the resumed context has continuity,
    /// not just the summary). Uses the real source_message_count and source_last_message_key.
    /// Returns the written path, or null on failure.
    /// </summary>
    public static string? WriteCompactionFileRich(
        string sessionId,
        string summary,
        int sourceMessageCount,
        string sourceLastMessageKey,
        System.Text.Json.Nodes.JsonArray? recentTail)
    {
        try
        {
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var sessionDir = Path.Combine(homeDir, ".cline", "data", "sessions", sessionId);
            var compactionPath = Path.Combine(sessionDir, $"{sessionId}.compaction.json");
            Directory.CreateDirectory(sessionDir);

            var sourcePrefixHash = ComputeSha256Hash(summary);

            var messages = new System.Text.Json.Nodes.JsonArray
            {
                new System.Text.Json.Nodes.JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new System.Text.Json.Nodes.JsonArray
                    {
                        new System.Text.Json.Nodes.JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = "Context summary:\n\n" + summary,
                        },
                    },
                    ["metadata"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["kind"] = "compaction_summary",
                        ["displayRole"] = "system",
                        ["userRunSpan"] = 1,
                        ["summary"] = summary,
                    },
                },
            };

            // Append the recent-message tail (Cline's native format includes recent turns after
            // the summary for continuity). We must CLONE each tail message before adding it to
            // the messages array, because a JsonNode can only have one parent at a time. Adding
            // a node that already belongs to the recentTail array would throw
            // InvalidOperationException ("The node already has a parent").
            if (recentTail is not null)
            {
                foreach (var msg in recentTail)
                {
                    messages.Add(msg?.DeepClone());
                }
            }

            var compaction = new System.Text.Json.Nodes.JsonObject
            {
                ["version"] = 1,
                ["updated_at"] = DateTime.UtcNow.ToString("o"),
                ["conversation_id"] = sessionId,
                ["source_message_count"] = sourceMessageCount,
                ["source_prefix_hash"] = "sha256:" + sourcePrefixHash,
                ["source_last_message_key"] = sourceLastMessageKey,
                ["messages"] = messages,
            };

            File.WriteAllText(compactionPath, compaction.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            }));

            System.Diagnostics.Debug.WriteLine("[ClineCompaction] Wrote rich compaction file: " + compactionPath);
            return compactionPath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[ClineCompaction] Failed to write rich compaction file: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Gets the most recent Cline session ID.
    /// </summary>
    /// <returns>The session ID, or null if no sessions found.</returns>
    public static string? GetMostRecentSessionId()
    {
        try
        {
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var sessionsDir = Path.Combine(homeDir, ".cline", "data", "sessions");

            if (!Directory.Exists(sessionsDir))
                return null;

            var dirs = Directory.GetDirectories(sessionsDir)
                .Select(d => new { Path = d, Time = Directory.GetLastWriteTime(d) })
                .OrderByDescending(x => x.Time)
                .ToList();

            if (dirs.Count == 0)
                return null;

            return Path.GetFileName(dirs[0].Path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClineCompaction] Failed to get most recent session ID: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The newest of the most recent <paramref name="maxSessions"/> Cline tasks whose chat file holds
    /// <paramref name="text"/> (Hail Mary uses the chat's opening request). Cline stores its chat as
    /// JSON, so the text is looked for with quotes and backslashes escaped as JSON escapes them.
    /// Null when none does. Hail Mary used to take the newest task only, which after another task
    /// was opened was the wrong one (26 Nov).
    /// </summary>
    public static string? FindRecentSessionContaining(string text, int maxSessions = 20)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var probe = text.Replace("\\", "\\\\").Replace("\"", "\\\"");
        try
        {
            var sessionsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cline", "data", "sessions");
            if (!Directory.Exists(sessionsDir))
            {
                return null;
            }

            foreach (var dir in Directory.GetDirectories(sessionsDir).OrderByDescending(Directory.GetLastWriteTimeUtc).Take(maxSessions))
            {
                var sessionId = Path.GetFileName(dir);
                var messagesPath = Path.Combine(dir, sessionId + ".messages.json");
                if (File.Exists(messagesPath) && File.ReadAllText(messagesPath).Contains(probe, StringComparison.Ordinal))
                {
                    return sessionId;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[ClineCompaction] Failed to search sessions: " + ex.Message);
        }

        return null;
    }

    /// <summary>
    /// Gets the on-disk directory for a Cline session (where its .messages.json and
    /// .compaction.json live). Used by the rescue path to locate the session's message history.
    /// </summary>
    public static string GetSessionDir(string sessionId)
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDir, ".cline", "data", "sessions", sessionId);
    }

    /// <summary>
    /// Matches the incoming request's messages to the on-disk Cline session that owns them.
    /// This is more reliable than <see cref="GetMostRecentSessionId"/> when multiple Cline sessions
    /// are open, because it identifies the session by its actual message content rather than by
    /// last-write-time. The incoming request's messages are a suffix of the on-disk session's
    /// messages, so we match on the last few message IDs (which are stable and unique per message).
    ///
    /// Strategy (fast path first):
    ///   1. Try the most-recent session (by last-write-time) — usually the active one.
    ///   2. If no match, scan the next most-recent sessions (bounded to keep it fast).
    ///   3. Return the session whose messages contain the request's last message ID.
    ///
    /// Returns null if no session matches (e.g., the request is from a non-Cline client, or the
    /// session hasn't been written to disk yet).
    /// </summary>
    public static string? MatchSessionIdForRequest(JsonArray? requestMessages)
    {
        if (requestMessages is null || requestMessages.Count == 0)
        {
            return null;
        }

        // Collect the last few message IDs from the request (these are the most recent and most
        // likely to be unique). We use the last 3 to be robust against a single ID collision.
        var requestIds = new List<string>();
        for (int i = Math.Max(0, requestMessages.Count - 3); i < requestMessages.Count; i++)
        {
            var msg = requestMessages[i] as JsonObject;
            if (msg is null) continue;
            var id = msg["id"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(id))
            {
                requestIds.Add(id.Trim());
            }
        }

        if (requestIds.Count == 0)
        {
            // No stable IDs in the request — fall back to most-recent (can't do better).
            return GetMostRecentSessionId();
        }

        try
        {
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var sessionsDir = Path.Combine(homeDir, ".cline", "data", "sessions");
            if (!Directory.Exists(sessionsDir))
            {
                return null;
            }

            var dirs = Directory.GetDirectories(sessionsDir)
                .Select(d => new { Path = d, Time = Directory.GetLastWriteTime(d) })
                .OrderByDescending(x => x.Time)
                .Take(10) // Bounded: only scan the 10 most-recent sessions.
                .ToList();

            foreach (var dir in dirs)
            {
                var sessionId = Path.GetFileName(dir.Path);
                var messagesPath = Path.Combine(dir.Path, sessionId + ".messages.json");
                if (!File.Exists(messagesPath))
                {
                    continue;
                }

                var diskMessages = ReadMessagesArray(messagesPath);
                if (diskMessages is null || diskMessages.Count == 0)
                {
                    continue;
                }

                // Check if the request's last message ID appears in this session's messages.
                // We only need to check the last request ID (the most recent one) — if it's here,
                // this is the session. Checking all 3 would be more robust but slower; the last
                // ID is almost always unique enough.
                var lastRequestId = requestIds[requestIds.Count - 1];
                bool found = false;
                // Scan from the end of the disk messages (the request's last message should be
                // near the end of the on-disk messages, since the request is a suffix).
                for (int i = diskMessages.Count - 1; i >= 0 && i >= diskMessages.Count - 50; i--)
                {
                    var diskMsg = diskMessages[i] as JsonObject;
                    if (diskMsg is null) continue;
                    var diskId = diskMsg["id"]?.GetValue<string>();
                    if (diskId == lastRequestId)
                    {
                        found = true;
                        break;
                    }
                }

                if (found)
                {
                    return sessionId;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Replaces the session's .messages.json with a compacted version (summary + recent tail).
    /// This is the "nuclear" option for a truly stuck Cline chat: when Cline is unresponsive and
    /// can't process a new turn, the .compaction.json file alone won't help because Cline only
    /// reads it on a subsequent turn. By directly replacing the .messages.json file, we ensure
    /// that when Cline reloads the session (close/reopen the chat, or restart Cline), it resumes
    /// with the compacted context immediately.
    /// </summary>
    /// <param name="sessionId">The Cline session ID.</param>
    /// <param name="summary">The compacted summary to include as the first message.</param>
    /// <param name="recentTail">Optional recent messages to append after the summary (for continuity).</param>
    /// <returns>The path to the written .messages.json file, or null if it failed.</returns>
    public static string? WriteCompactedMessagesFile(string sessionId, string summary, JsonArray? recentTail = null)
    {
        try
        {
            var sessionDir = GetSessionDir(sessionId);
            var messagesPath = Path.Combine(sessionDir, $"{sessionId}.messages.json");

            if (!Directory.Exists(sessionDir))
                return null;

            // Read the original .messages.json to preserve the top-level metadata (version, agent, origin, etc.)
            JsonObject original = null;
            if (File.Exists(messagesPath))
            {
                try
                {
                    original = JsonObject.Parse(File.ReadAllText(messagesPath)) as JsonObject;
                }
                catch
                {
                    // If we can't parse the original, we'll build a minimal one.
                }
            }

            // Build the compacted messages array: summary message + recent tail.
            var messages = new JsonArray();

            // The summary message (marked as a compaction summary so Cline knows it's a compacted context).
            var summaryMessage = new JsonObject
            {
                ["id"] = "compaction-" + Guid.NewGuid().ToString("N"),
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = "Context summary:\n\n" + summary
                    }
                },
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["metadata"] = new JsonObject
                {
                    ["kind"] = "compaction_summary",
                    ["displayRole"] = "system",
                    ["userRunSpan"] = 1,
                    ["summary"] = summary
                }
            };
            messages.Add(summaryMessage);

            // Append the recent-message tail (for continuity). We must CLONE each tail message
            // before adding it to the messages array, because a JsonNode can only have one parent
            // at a time. Adding a node that already belongs to the recentTail array would throw
            // InvalidOperationException ("The node already has a parent").
            if (recentTail is not null)
            {
                foreach (var msg in recentTail)
                {
                    messages.Add(msg?.DeepClone());
                }
            }

            // Build the new .messages.json object, preserving the original top-level metadata.
            var compacted = new JsonObject
            {
                ["version"] = 1,
                ["updated_at"] = DateTime.UtcNow.ToString("o"),
                ["sessionId"] = sessionId,
                ["messages"] = messages,
            };

            // Preserve the original top-level fields (agent, origin, etc.) if they exist.
            if (original is not null)
            {
                if (original.TryGetPropertyValue("agent", out var agent))
                    compacted["agent"] = agent;
                if (original.TryGetPropertyValue("origin", out var origin))
                    compacted["origin"] = origin;
            }

            File.WriteAllText(messagesPath, compacted.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            }));

            System.Diagnostics.Debug.WriteLine("[ClineCompaction] Wrote compacted .messages.json: " + messagesPath);
            return messagesPath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[ClineCompaction] Failed to write compacted .messages.json: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Computes the SHA-256 hash of a string.
    /// </summary>
    private static string ComputeSha256Hash(string input)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder();
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }
}
