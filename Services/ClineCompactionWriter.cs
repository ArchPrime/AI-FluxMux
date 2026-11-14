using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    /// Writes a .compaction.json file for the given Cline session.
    /// </summary>
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
    /// Gets the on-disk directory for a Cline session (where its .messages.json and
    /// .compaction.json live). Used by the rescue path to locate the session's message history.
    /// </summary>
    public static string GetSessionDir(string sessionId)
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDir, ".cline", "data", "sessions", sessionId);
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
