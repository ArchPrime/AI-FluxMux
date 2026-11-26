using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace FluxMux.Avalonia.Services;

/// <summary>One saved chat: the latest one a Client app sent through Port, exactly as it sent it.</summary>
public sealed record SavedChat(
    string Key,
    string? ClientApp,
    DateTime SentUtc,
    int ContextTokens,
    bool Overflowed,
    DateTime? OverflowedUtc,
    JsonArray Messages)
{
    /// <summary>"Cline chat, 11:30 (failed)".</summary>
    public string Label
        => (string.IsNullOrWhiteSpace(ClientApp) ? "Unrecognised Client app" : ClientApp) + " chat, "
           + SentUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
           + (Overflowed ? " (failed)" : string.Empty);
}

/// <summary>
/// The latest chat of each Client app that sends through Port (26 Nov), for the hand over brief and
/// Hail Mary. Several Client apps can share Port (pi, Cline, Harness, OpenCode…), so one "latest
/// chat" for all of them lost a failed Cline chat the moment pi sent anything — exactly when the
/// person needs it. Each is kept as its Client app sent it (before Omit, compaction or any other
/// rewrite), keyed by the name <see cref="ClientAppDetector"/> gives, with one shared slot for apps
/// it does not recognise. Each is also written to its own file in <c>Logs\saved_chats</c>, off the
/// request thread, so it survives a restart: a stuck chat may never send again.
///
/// A chat is marked failed when the gateway refuses it as too large (<see cref="MarkCurrentOverflowed"/>),
/// and the mark is cleared when that Client app sends again. The chat to act on by default is the
/// most recently failed one, else the most recent (<see cref="PickPreferred"/>).
/// </summary>
public static class SavedChats
{
    public const string UnrecognisedKey = "unrecognised";

    private static readonly ConcurrentDictionary<string, SavedChat> Chats = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, System.Runtime.CompilerServices.StrongBox<int>> WritesQueued = new(StringComparer.OrdinalIgnoreCase);
    private static readonly AsyncLocal<string?> CurrentKey = new();
    private static readonly object LoadGate = new();
    private static volatile string? _folder;
    private static volatile bool _loaded;
    private static volatile int _lastKnownContext;

    /// <summary>Where the chats are saved; loaded on first use. A single chat saved by the 26 Nov build is moved in.</summary>
    public static void Attach(string folder, string? legacyFile = null)
    {
        _folder = folder;
        _loaded = false;
        Chats.Clear();
        try
        {
            // The single chat file of the first 26 Nov build becomes its Client app's chat.
            if (legacyFile is not null && File.Exists(legacyFile))
            {
                Directory.CreateDirectory(folder);
                var target = Read(legacyFile) is { } chat ? FileFor(folder, chat.Key) : null;
                if (target is not null && !File.Exists(target))
                {
                    File.Move(legacyFile, target);
                }
                else
                {
                    File.Delete(legacyFile);
                }
            }
        }
        catch
        {
        }
    }

    public static string KeyFor(string? clientApp)
        => string.IsNullOrWhiteSpace(clientApp) ? UnrecognisedKey : clientApp.Trim();

    /// <summary>Keeps this request's chat as its Client app's latest, and remembers it as the current request's chat.</summary>
    public static void Keep(JsonArray messages, string? clientApp)
    {
        var key = KeyFor(clientApp);
        CurrentKey.Value = key;
        EnsureLoaded();
        Chats[key] = new SavedChat(key, clientApp, DateTime.UtcNow, _lastKnownContext, false, null, (JsonArray)messages.DeepClone());
        WriteSoon(key);
    }

    /// <summary>The loaded Context the gateway last saw; filled into a chat saved before it was known.</summary>
    public static void NoteContext(int contextTokens)
    {
        if (contextTokens <= 0)
        {
            return;
        }

        _lastKnownContext = contextTokens;
        if (CurrentKey.Value is { } key && Chats.TryGetValue(key, out var chat) && chat.ContextTokens == 0)
        {
            Chats[key] = chat with { ContextTokens = contextTokens };
            WriteSoon(key);
        }
    }

    /// <summary>Marks the current request's chat as refused for size.</summary>
    public static void MarkCurrentOverflowed()
    {
        if (CurrentKey.Value is { } key && Chats.TryGetValue(key, out var chat) && !chat.Overflowed)
        {
            Chats[key] = chat with { Overflowed = true, OverflowedUtc = DateTime.UtcNow };
            WriteSoon(key);
        }
    }

    public static bool Any
    {
        get
        {
            EnsureLoaded();
            return !Chats.IsEmpty;
        }
    }

    /// <summary>Every saved chat, newest first; each a copy the caller may change.</summary>
    public static IReadOnlyList<SavedChat> All()
    {
        EnsureLoaded();
        return Chats.Values.OrderByDescending(c => c.SentUtc).Select(Copy).ToList();
    }

    public static SavedChat? Get(string key)
    {
        EnsureLoaded();
        return Chats.TryGetValue(key, out var chat) ? Copy(chat) : null;
    }

    /// <summary>The chat to act on by default: the most recently failed one, else the most recent.</summary>
    public static SavedChat? Preferred() => PickPreferred(All());

    public static SavedChat? PickPreferred(IEnumerable<SavedChat> chats)
    {
        var list = chats.ToList();
        return list.Where(c => c.Overflowed).OrderByDescending(c => c.OverflowedUtc ?? c.SentUtc).FirstOrDefault()
               ?? list.OrderByDescending(c => c.SentUtc).FirstOrDefault();
    }

    /// <summary>The preferred chat's Client app, without copying any chat (cheap enough for a property).</summary>
    public static string? PreferredClientApp()
    {
        EnsureLoaded();
        return PickPreferred(Chats.Values)?.ClientApp;
    }

    /// <summary>The preferred chat's key, without copying any chat.</summary>
    public static string? PreferredKey()
    {
        EnsureLoaded();
        return PickPreferred(Chats.Values)?.Key;
    }

    /// <summary>Key, label and send time of every saved chat, newest first, without copying the messages.</summary>
    public static IReadOnlyList<(string Key, string Label, DateTime SentUtc)> Summaries()
    {
        EnsureLoaded();
        return Chats.Values.OrderByDescending(c => c.SentUtc).Select(c => (c.Key, c.Label, c.SentUtc)).ToList();
    }

    private static SavedChat Copy(SavedChat chat) => chat with { Messages = (JsonArray)chat.Messages.DeepClone() };

    private static void EnsureLoaded()
    {
        if (_loaded || _folder is not { } folder)
        {
            return;
        }

        lock (LoadGate)
        {
            if (_loaded)
            {
                return;
            }

            try
            {
                if (Directory.Exists(folder))
                {
                    foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
                    {
                        if (Read(file) is { } chat && !Chats.ContainsKey(chat.Key))
                        {
                            Chats[chat.Key] = chat;
                        }
                    }
                }
            }
            catch
            {
            }

            _loaded = true;
        }
    }

    private static SavedChat? Read(string file)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject saved || saved["messages"] is not JsonArray messages)
            {
                return null;
            }

            var app = saved["clientApp"]?.ToString();
            var clientApp = string.IsNullOrWhiteSpace(app) ? null : app;
            var sent = DateTime.TryParse(saved["sentUtc"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                ? at
                : File.GetLastWriteTimeUtc(file);
            DateTime? overflowedAt = DateTime.TryParse(saved["overflowedUtc"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var o) ? o : null;
            var context = int.TryParse(saved["contextTokens"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : 0;
            var overflowed = saved["overflowed"] is JsonValue flag && flag.TryGetValue<bool>(out var f) && f;
            return new SavedChat(KeyFor(clientApp), clientApp, sent, context, overflowed, overflowed ? overflowedAt ?? sent : null, (JsonArray)messages.DeepClone());
        }
        catch
        {
            return null;
        }
    }

    private static string FileFor(string folder, string key)
    {
        var name = new StringBuilder();
        foreach (var ch in key.ToLowerInvariant())
        {
            name.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        }

        return Path.Combine(folder, name + ".json");
    }

    /// <summary>
    /// One write in flight per Client app, always of its newest chat: a Client app that sends turns
    /// quickly must not queue a multi-megabyte write per turn.
    /// </summary>
    private static void WriteSoon(string key)
    {
        var queued = WritesQueued.GetOrAdd(key, _ => new System.Runtime.CompilerServices.StrongBox<int>());
        if (_folder is not { } folder || Interlocked.Exchange(ref queued.Value, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                Interlocked.Exchange(ref queued.Value, 0);
                if (!Chats.TryGetValue(key, out var chat))
                {
                    return;
                }

                var record = new JsonObject
                {
                    ["clientApp"] = chat.ClientApp ?? string.Empty,
                    ["sentUtc"] = chat.SentUtc.ToString("o", CultureInfo.InvariantCulture),
                    ["contextTokens"] = chat.ContextTokens,
                    ["overflowed"] = chat.Overflowed,
                    ["overflowedUtc"] = chat.OverflowedUtc?.ToString("o", CultureInfo.InvariantCulture),
                    ["messages"] = chat.Messages.DeepClone(),
                };
                Directory.CreateDirectory(folder);
                var path = FileFor(folder, key);
                // A unique temp name: a second write can start before this one has moved its file.
                var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tmp, record.ToJsonString());
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
            }
        });
    }
}
