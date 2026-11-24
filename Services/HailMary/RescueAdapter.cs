using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services.HailMary;

/// <summary>
/// One Client app's "can't continue" rescue, described as data so a new Client app can be added
/// by configuration instead of code. The technique is the same for every Client app that rebuilds
/// its chat from a file it reads by itself: find the stuck chat's file, stop the Client app if it
/// holds that file, back it up, write AI-FluxMux's summary into it in the Client app's own format,
/// then reopen the same chat. Only the specifics differ, and those live here.
///
/// Listed in fluxmux_config.json under <c>RescueAdapters</c> (see rescue-adapters.example.json).
/// A listed id replaces the built-in with that id; <c>enabled: false</c> turns one off.
/// </summary>
public sealed record RescueAdapterDefinition
{
    public string Id { get; init; } = string.Empty;

    /// <summary>Text of the Hail Mary picker option.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Client app names (the Client app dropdown) this rescue is offered for.</summary>
    public IReadOnlyList<string> ClientApps { get; init; } = Array.Empty<string>();

    public bool Enabled { get; init; } = true;

    /// <summary>Recent messages the Client app keeps word for word after the summary ({keepRecent}).</summary>
    public int KeepRecent { get; init; } = 6;

    public RescueFind Find { get; init; } = new();

    /// <summary>none | harness-web | process.</summary>
    public RescueAction Stop { get; init; } = new();

    /// <summary>Copy the chat file to "&lt;file&gt;.hailmary-&lt;time&gt;.bak" before any step.</summary>
    public bool Backup { get; init; } = true;

    public IReadOnlyList<RescueStep> Steps { get; init; } = Array.Empty<RescueStep>();

    /// <summary>none | harness-web | url | command.</summary>
    public RescueAction Reopen { get; init; } = new();

    /// <summary>Told to the user when every step succeeded.</summary>
    public string DoneMessage { get; init; } = string.Empty;

    /// <summary>True for the built-in Cline rescue, which keeps its own hash-checked writer.</summary>
    public bool IsBuiltInCline => Steps.Any(s => s.Kind.Equals(RescueStep.BuiltInKind, StringComparison.OrdinalIgnoreCase)
        && s.Name.Equals(RescueAdapterCatalog.ClineCompactionStep, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Where the Client app keeps its chats. The newest matching file is the stuck chat.</summary>
public sealed record RescueFind
{
    /// <summary>Folder searched; environment variables such as %USERPROFILE% are expanded.</summary>
    public string Root { get; init; } = string.Empty;

    /// <summary>Chat folders under Root, e.g. "*\session-*" (one wildcard level per segment).</summary>
    public string Dirs { get; init; } = "*";

    /// <summary>The chat file inside a chat folder, e.g. "session.v*.jsonl*".</summary>
    public string File { get; init; } = "*";
}

/// <summary>A stop or reopen action.</summary>
public sealed record RescueAction
{
    public string Kind { get; init; } = "none";

    /// <summary>process: image name without .exe. url: the address. command: the program.</summary>
    public string Target { get; init; } = string.Empty;

    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

    public int WaitSeconds { get; init; } = 10;

    public bool IsNone => string.IsNullOrWhiteSpace(Kind) || Kind.Equals("none", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One step. Kinds: command (run a program; Exe, Args, TimeoutSeconds, ExpectJsonOk),
/// write-file (Path, Content), delete (Path; a missing file is fine), builtin (Name).
/// Text fields take placeholders: {summary}, {summaryJson}, {summaryFile}, {sessionId},
/// {sessionDir}, {sessionFile}, {keepRecent}, {rescueScripts}, {nowMs}, plus %ENV% variables.
/// </summary>
public sealed record RescueStep
{
    public const string CommandKind = "command";
    public const string WriteFileKind = "write-file";
    public const string DeleteKind = "delete";
    public const string BuiltInKind = "builtin";

    public string Kind { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Exe { get; init; } = string.Empty;
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();
    public string Path { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public int TimeoutSeconds { get; init; } = 120;

    /// <summary>The program prints a JSON line with "ok": true when it worked.</summary>
    public bool ExpectJsonOk { get; init; }
}

public static class RescueAdapterCatalog
{
    public const string ConfigKey = "RescueAdapters";
    public const string ClineId = "cline-compaction-file";
    public const string HarnessId = "harness-session-log";
    public const string ClineCompactionStep = "cline-compaction";
    public const string HarnessWebAction = "harness-web";

    public static IReadOnlyList<RescueAdapterDefinition> BuiltIns { get; } =
    [
        new RescueAdapterDefinition
        {
            Id = ClineId,
            Label = "Cline can't continue — compact externally & resurrect",
            ClientApps = ["Cline"],
            Find = new RescueFind { Root = @"%USERPROFILE%\.cline\data\sessions", Dirs = "*", File = "*.messages.json" },
            Backup = false,
            Steps = [new RescueStep { Kind = RescueStep.BuiltInKind, Name = ClineCompactionStep }],
            DoneMessage = "External Cline compaction written. To apply it, close and reopen this Cline chat (or restart Cline) so the session reloads with the compacted context.",
        },
        new RescueAdapterDefinition
        {
            Id = HarnessId,
            Label = "Harness can't continue — rebuild & reopen this chat",
            ClientApps = ["DeepSeek Harness"],
            Find = new RescueFind { Root = @"%USERPROFILE%\.dsh\sessions", Dirs = @"*\session-*", File = "session.v4.jsonl.zstd" },
            Stop = new RescueAction { Kind = HarnessWebAction, WaitSeconds = 15 },
            Steps =
            [
                new RescueStep
                {
                    Kind = RescueStep.CommandKind,
                    Exe = "node",
                    Args = [@"{rescueScripts}\dsh-rescue.mjs", "--log", "{sessionFile}", "--summary-file", "{summaryFile}", "--keep", "{keepRecent}"],
                    TimeoutSeconds = 120,
                    ExpectJsonOk = true,
                },
                // Harness caches what its chat list shows; drop the stale copy so it rebuilds it.
                new RescueStep { Kind = RescueStep.DeleteKind, Path = @"%USERPROFILE%\.dsh\storages\session_projcache\sessions\{sessionId}.json" },
            ],
            Reopen = new RescueAction { Kind = HarnessWebAction },
            DoneMessage = "Harness was restarted on the same chat. The older part of the chat is now a summary from AI-FluxMux, and the most recent messages are kept — carry on in that chat.",
        },
    ];

    public static IReadOnlyList<RescueAdapterDefinition> Resolve(IReadOnlyList<JsonObject>? listed)
    {
        var byId = new Dictionary<string, RescueAdapterDefinition>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var builtIn in BuiltIns)
        {
            byId[builtIn.Id] = builtIn;
            order.Add(builtIn.Id);
        }

        foreach (var node in listed ?? Array.Empty<JsonObject>())
        {
            var parsed = Parse(node);
            if (string.IsNullOrWhiteSpace(parsed.Id))
            {
                continue;
            }

            if (!byId.ContainsKey(parsed.Id))
            {
                order.Add(parsed.Id);
            }

            byId[parsed.Id] = parsed;
        }

        return order.Select(id => byId[id]).ToList();
    }

    /// <summary>The first enabled rescue offered for this Client app, or null.</summary>
    public static RescueAdapterDefinition? ForClientApp(IReadOnlyList<RescueAdapterDefinition> adapters, string? clientApp)
        => adapters.FirstOrDefault(a => a.Enabled
            && a.Steps.Count > 0
            && a.ClientApps.Any(app => app.Equals((clientApp ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)));

    internal static RescueAdapterDefinition Parse(JsonObject node)
    {
        var find = node["find"] as JsonObject;
        return new RescueAdapterDefinition
        {
            Id = Text(node, "id"),
            Label = Text(node, "label"),
            ClientApps = Strings(node["clientApps"]),
            Enabled = Bool(node, "enabled", true),
            KeepRecent = Math.Clamp(Int(node, "keepRecent", 6), 2, 200),
            Find = new RescueFind
            {
                Root = find is null ? string.Empty : Text(find, "root"),
                Dirs = find is null ? "*" : FirstNonEmpty(Text(find, "dirs"), "*"),
                File = find is null ? "*" : FirstNonEmpty(Text(find, "file"), "*"),
            },
            Stop = Action(node["stop"] as JsonObject),
            Backup = Bool(node, "backup", true),
            Steps = (node["steps"] as JsonArray)?.OfType<JsonObject>().Select(ParseStep).ToList() ?? new List<RescueStep>(),
            Reopen = Action(node["reopen"] as JsonObject),
            DoneMessage = Text(node, "doneMessage"),
        };
    }

    private static RescueStep ParseStep(JsonObject node) => new()
    {
        Kind = Text(node, "kind"),
        Name = Text(node, "name"),
        Exe = Text(node, "exe"),
        Args = Strings(node["args"]),
        Path = Text(node, "path"),
        Content = node["content"]?.ToString() ?? string.Empty,
        TimeoutSeconds = Math.Clamp(Int(node, "timeoutSeconds", 120), 1, 1800),
        ExpectJsonOk = Bool(node, "expectJsonOk", false),
    };

    private static RescueAction Action(JsonObject? node) => node is null
        ? new RescueAction()
        : new RescueAction
        {
            Kind = FirstNonEmpty(Text(node, "kind"), "none"),
            Target = Text(node, "target"),
            Args = Strings(node["args"]),
            WaitSeconds = Math.Clamp(Int(node, "waitSeconds", 10), 0, 300),
        };

    private static string Text(JsonObject node, string key) => (node[key]?.ToString() ?? string.Empty).Trim();

    private static string FirstNonEmpty(string a, string b) => string.IsNullOrWhiteSpace(a) ? b : a;

    private static IReadOnlyList<string> Strings(JsonNode? node) => node switch
    {
        JsonArray array => array.Select(n => n?.ToString() ?? string.Empty).Where(s => s.Length > 0).ToList(),
        JsonValue value when !string.IsNullOrWhiteSpace(value.ToString()) => [value.ToString()],
        _ => Array.Empty<string>(),
    };

    private static bool Bool(JsonObject node, string key, bool fallback)
        => node[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    private static int Int(JsonObject node, string key, int fallback)
    {
        if (node[key] is not JsonValue v)
        {
            return fallback;
        }

        if (v.TryGetValue<int>(out var i))
        {
            return i;
        }

        return int.TryParse(v.ToString(), out var parsed) ? parsed : fallback;
    }
}
