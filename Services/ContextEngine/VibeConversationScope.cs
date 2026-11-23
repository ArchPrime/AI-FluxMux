using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FluxMux.Avalonia.Services.ContextEngine;

/// <summary>
/// Keeps VIBE_STATE.md and essence.json to ONE conversation at a time.
///
/// There used to be a single pair of files for every chat, project and Client app, so a Harness
/// chat about project B was fed project A's task in its prompt. The files keep their fixed paths
/// (Hail Mary, the MCP tool, the reasserter and the UI all read them), but they now hold the
/// current conversation only. When a different conversation starts, the current pair is moved
/// to <c>vibe-archive/&lt;key&gt;.*</c> and the new conversation's archived pair (if it was seen
/// before) is brought back, so returning to an older chat restores its state.
///
/// The key comes from the caller (Cline session id, else the first human message), and the
/// caller decides when a change really is a new conversation rather than a compaction — see
/// FluxMuxGatewayHost.EnsureVibeConversationScope. Never throws: a failed swap leaves the
/// current files in place, which is the old behaviour.
/// </summary>
public sealed class VibeConversationScope
{
    private const string ScopeFileName = "vibe-scope.txt";
    private const string ArchiveDirName = "vibe-archive";
    internal const string LegacyKey = "legacy-before-scoping";

    private readonly string _vibePath;
    private readonly string _essencePath;
    private readonly string _scopePath;
    private readonly string _archiveDir;
    private readonly object _gate = new();

    public VibeConversationScope(string vibePath, string essencePath)
    {
        _vibePath = vibePath;
        _essencePath = essencePath;
        var dir = Path.GetDirectoryName(vibePath) ?? ".";
        _scopePath = Path.Combine(dir, ScopeFileName);
        _archiveDir = Path.Combine(dir, ArchiveDirName);
    }

    /// <summary>The key of the conversation the current files belong to, or null if unknown.</summary>
    public string? CurrentKey
    {
        get
        {
            try
            {
                return File.Exists(_scopePath) ? File.ReadAllText(_scopePath).Trim() : null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Makes the current files belong to <paramref name="key"/>. Returns true when it switched
    /// (archived the previous conversation and restored or cleared the files).
    /// </summary>
    public bool Ensure(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        lock (_gate)
        {
            try
            {
                var current = CurrentKey;
                if (string.Equals(current, key, StringComparison.Ordinal))
                {
                    return false;
                }

                Directory.CreateDirectory(_archiveDir);
                // Files written before scoping existed mix every chat; keep them, but apart.
                var outgoing = string.IsNullOrWhiteSpace(current) ? LegacyKey : current;
                MoveIfExists(_vibePath, ArchivePath(outgoing, ".md"));
                MoveIfExists(_essencePath, ArchivePath(outgoing, ".json"));
                if (!string.Equals(key, LegacyKey, StringComparison.Ordinal))
                {
                    MoveIfExists(ArchivePath(key, ".md"), _vibePath);
                    MoveIfExists(ArchivePath(key, ".json"), _essencePath);
                }

                File.WriteAllText(_scopePath, key);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>True when a conversation with this key was seen before and archived.</summary>
    public bool HasArchive(string key)
    {
        try
        {
            return File.Exists(ArchivePath(key, ".md")) || File.Exists(ArchivePath(key, ".json"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A short, file-name-safe key for a piece of text (first human message).</summary>
    public static string KeyFor(string prefix, string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ConversationText.Normalize(text)));
        return prefix + Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    private string ArchivePath(string key, string extension)
    {
        var safe = string.Concat(key.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_archiveDir, safe + extension);
    }

    private static void MoveIfExists(string from, string to)
    {
        if (File.Exists(from))
        {
            File.Move(from, to, overwrite: true);
        }
    }
}
