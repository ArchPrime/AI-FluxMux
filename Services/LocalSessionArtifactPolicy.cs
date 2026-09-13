using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Session-created diagnostic dumps stay in the Client-app chat. They are
/// omitted from the pack forwarded to llama-server so later overlays cannot
/// push out observation screenshots or the current project files.
/// </summary>
public static class LocalSessionArtifactPolicy
{
    public const int EditLoopThreshold = 4;
    public const int HaltThreshold = 6;
    public const int MaxNewWritesPerReply = 8;
    public const string HaltType = "cline_diagnostic_dump";
    public const string HaltMessage =
        PortRulesPostMortem.ChatTurnCannotContinue
        + "the Client app created too many session diagnostic files. llama-server was not asked.";
    public const string DroppedWritesNote =
        "Do not create diagnostic files. Edit the project source.";
    public const string OmittedMark = LocalToolResultClearing.OmittedMark;
    public const string ForwardNote =
        "AI-FluxMux did not forward session-created diagnostic files to llama-server. This turn can see the last observation pictures and the current project files. Edit the project source; do not write new analysis files.";
    public const string EditLoopNote =
        "This project file has been rewritten several times this chat. llama-server only has the current body. Change one thing; do not add diagnostic files.";

    private static readonly string[] WriteCreateTools =
    [
        "write_to_file",
        "write",
        "write_file",
        "create_file"
    ];

    private static readonly string[] EditTools =
    [
        "replace_in_file",
        "search_replace",
        "apply_diff",
        "apply_patch"
    ];

    private static readonly string[] ReadTools =
    [
        "read_file",
        "read"
    ];

    private static readonly string[] ObservationTools =
    [
        "browser_action",
        "browser_take_screenshot",
        "take_screenshot",
        "screenshot",
        "capture_screenshot"
    ];

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".axaml", ".xaml", ".fs", ".vb",
        ".js", ".ts", ".tsx", ".jsx", ".mjs", ".cjs",
        ".py", ".go", ".rs", ".java", ".kt", ".swift",
        ".c", ".cpp", ".cc", ".h", ".hpp", ".m", ".mm",
        ".html", ".htm", ".css", ".scss", ".sass", ".less",
        ".vue", ".svelte", ".lua", ".gd", ".gdl",
        ".php", ".rb", ".sh", ".ps1", ".bat", ".cmd"
    };

    private static readonly Regex DumpNameRegex = new(
        @"(diagnos|debug|overlay|dump|trace|inspect|wireframe|vanishing|analysis|axes?[-_]|hud[-_])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LoopRegex = new(
        @"\bfor\s*\(|\bfor\s+\w+\s+in\b|\bforeach\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ImageExtensionRegex = new(
        @"\.(?:png|jpe?g|gif|webp|bmp)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FileWriteRegex = new(
        @"\bOut-File\b|\bSet-Content\b|\bAdd-Content\b|\bNew-Item\b|\btee\b|fs\.writeFile|\bopen\s*\(| >\s*\S+\.(png|jpe?g|md|json|txt|html|log|csv)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex XmlToolPathRegex = new(
        @"<(?<tag>write_to_file|write|write_file|create_file|replace_in_file|search_replace|apply_diff|apply_patch|read_file|read)\b[\s\S]*?<(?:path|file_path|target_file|target|file)>(?<path>[^<]+)</(?:path|file_path|target_file|target|file)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static LocalSessionArtifacts Inspect(JsonArray? messages)
    {
        var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var edits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (messages is null)
        {
            return new LocalSessionArtifacts(created, edits);
        }

        foreach (var message in messages.OfType<JsonObject>())
        {
            foreach (var (name, path) in ListToolPaths(message))
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (IsReadTool(name))
                {
                    read.Add(path);
                    continue;
                }

                if (IsWriteCreateTool(name))
                {
                    if (!read.Contains(path))
                    {
                        created.Add(path);
                    }

                    edits[path] = edits.GetValueOrDefault(path) + 1;
                    continue;
                }

                if (IsEditTool(name))
                {
                    edits[path] = edits.GetValueOrDefault(path) + 1;
                }
            }
        }

        return new LocalSessionArtifacts(created, edits);
    }

    public static LocalSessionApplyResult Apply(JsonObject payload)
    {
        if (payload["messages"] is not JsonArray messages)
        {
            return default;
        }

        var artifacts = Inspect(messages);
        var omitted = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject result || !LocalToolResultClearing.IsToolResult(result))
            {
                continue;
            }

            var text = ExtractText(result["content"]);
            if (text.StartsWith(OmittedMark, StringComparison.Ordinal))
            {
                continue;
            }

            var call = LocalToolResultClearing.FindMatchingCall(messages, i);
            if (IsObservationTool(call))
            {
                continue;
            }

            var path = call is null ? string.Empty : ReadToolPath(call);
            if (!artifacts.IsArtifact(path))
            {
                continue;
            }

            var hadPicture = HasImagePart(result["content"]);
            if (!hadPicture && text.Length < LocalToolResultClearing.MinResultCharsToClear)
            {
                continue;
            }

            result["content"] = FormatStub(result, hadPicture);
            omitted++;
        }

        var note = omitted > 0;
        var editLoop = artifacts.HottestEditCount >= EditLoopThreshold;
        if (note || editLoop)
        {
            AppendForwardNote(payload, editLoop);
        }

        return new LocalSessionApplyResult(omitted, artifacts.HottestEditCount, artifacts.HottestPath, artifacts.ArtifactCount);
    }

    public static string FormatHaltMessage(JsonObject? state)
    {
        _ = state;
        return PortRulesPostMortem.WithStopAdvice(HaltMessage);
    }

    public static int FilterCompletion(JsonObject root, LocalSessionArtifacts? artifacts)
    {
        if (root["choices"] is not JsonArray choices)
        {
            return 0;
        }

        var dropped = 0;
        foreach (var node in choices.OfType<JsonObject>())
        {
            dropped += FilterMessageToolCalls(node["message"] as JsonObject, artifacts);
            dropped += FilterMessageToolCalls(node["delta"] as JsonObject, artifacts);
        }

        return dropped;
    }

    public static int FilterMessageToolCalls(JsonObject? message, LocalSessionArtifacts? artifacts)
    {
        _ = artifacts;
        if (message?["tool_calls"] is not JsonArray calls || calls.Count == 0)
        {
            return 0;
        }

        var dropped = 0;
        var keptWrites = 0;
        for (var i = 0; i < calls.Count; i++)
        {
            if (calls[i] is not JsonObject call)
            {
                continue;
            }

            if (!ShouldBlockOutgoingCall(call, keptWrites))
            {
                if (IsWriteCreateTool(ReadToolName(call)))
                {
                    keptWrites++;
                }

                continue;
            }

            calls.RemoveAt(i);
            i--;
            dropped++;
        }

        if (calls.Count == 0)
        {
            message.Remove("tool_calls");
            if (string.IsNullOrWhiteSpace(ExtractText(message["content"])))
            {
                message["content"] = DroppedWritesNote;
            }
        }

        return dropped;
    }

    public static bool ShouldBlockOutgoingCall(JsonObject call, int keptWrites)
    {
        var name = ReadToolName(call);
        if (IsWriteCreateTool(name))
        {
            var path = ReadToolPath(call);
            if (string.IsNullOrWhiteSpace(path)
                || IsImagePath(path)
                || (IsDumpNamed(path) && !IsSourcePath(path))
                || keptWrites >= MaxNewWritesPerReply)
            {
                return true;
            }

            return false;
        }

        if (!IsShellTool(name))
        {
            return false;
        }

        var command = ReadShellCommand(call);
        return LooksLikeBulkFileCreate(command);
    }

    public static bool LooksLikeBulkFileCreate(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || !LoopRegex.IsMatch(command) || !LooksLikeFileWrite(command))
        {
            return false;
        }

        return HasImageExtension(command) || IsDumpNamed(command);
    }

    public static bool LooksLikeFileWrite(string command)
        => !string.IsNullOrWhiteSpace(command) && FileWriteRegex.IsMatch(command);

    public static bool IsWriteCreateTool(string? name)
        => WriteCreateTools.Any(tool => tool.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool IsShellTool(string name)
        => name.Equals("execute_command", StringComparison.OrdinalIgnoreCase)
           || name.Equals("run_command", StringComparison.OrdinalIgnoreCase)
           || name.Equals("shell", StringComparison.OrdinalIgnoreCase)
           || name.Equals("bash", StringComparison.OrdinalIgnoreCase)
           || name.Equals("run_terminal_cmd", StringComparison.OrdinalIgnoreCase);

    private static string ReadShellCommand(JsonObject call)
        => FirstNonEmpty(
            ReadArgField(call["function"]?["arguments"] ?? call["arguments"],
                "command", "cmd", "input"));

    public static bool IsObservationTool(JsonObject? call)
        => IsObservationToolName(call is null ? string.Empty : ReadToolName(call));

    public static bool IsObservationToolName(string? name)
    {
        var value = (name ?? string.Empty).Trim();
        return ObservationTools.Any(tool => tool.Equals(value, StringComparison.OrdinalIgnoreCase));
    }

    public static string ReadToolName(JsonObject call)
        => (call["function"]?["name"]?.ToString()
            ?? call["name"]?.ToString()
            ?? string.Empty).Trim();

    public static string ReadToolPath(JsonObject call)
        => FirstNonEmpty(
            ReadArgField(call["function"]?["arguments"] ?? call["arguments"],
                "path", "file_path", "file", "target", "target_file"));

    public static bool IsDumpNamed(string path)
    {
        var name = Path.GetFileName(path ?? string.Empty);
        return !string.IsNullOrWhiteSpace(name) && DumpNameRegex.IsMatch(name);
    }

    public static bool IsSourcePath(string path)
    {
        var ext = Path.GetExtension(path ?? string.Empty);
        return !string.IsNullOrWhiteSpace(ext) && SourceExtensions.Contains(ext);
    }

    public static bool IsImagePath(string path)
        => HasImageExtension(path);

    private static bool HasImageExtension(string text)
        => !string.IsNullOrWhiteSpace(text) && ImageExtensionRegex.IsMatch(text);

    private static void AppendForwardNote(JsonObject payload, bool editLoop)
    {
        var lastUser = LastRealUserMessage(payload);
        if (lastUser is null)
        {
            return;
        }

        AppendNote(lastUser, ForwardNote);
        if (editLoop)
        {
            AppendNote(lastUser, EditLoopNote);
        }
    }

    private static void AppendNote(JsonObject message, string note)
    {
        if (message["content"] is JsonArray parts)
        {
            foreach (var part in parts.OfType<JsonObject>())
            {
                if (string.Equals(part["text"]?.ToString(), note, StringComparison.Ordinal))
                {
                    return;
                }
            }

            parts.Add(new JsonObject { ["type"] = "text", ["text"] = note });
            return;
        }

        var text = message["content"]?.ToString() ?? string.Empty;
        if (text.Contains(note, StringComparison.Ordinal))
        {
            return;
        }

        message["content"] = string.IsNullOrWhiteSpace(text) ? note : text.TrimEnd() + "\n" + note;
    }

    private static JsonObject? LastRealUserMessage(JsonObject payload)
    {
        if (payload["messages"] is not JsonArray messages)
        {
            return null;
        }

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject msg)
            {
                continue;
            }

            if (!string.Equals(msg["role"]?.ToString(), "user", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (LocalToolResultClearing.IsToolResult(msg))
            {
                continue;
            }

            return msg;
        }

        return null;
    }

    private static IEnumerable<(string Name, string Path)> ListToolPaths(JsonObject message)
    {
        var found = false;
        if (message["tool_calls"] is JsonArray calls)
        {
            foreach (var node in calls.OfType<JsonObject>())
            {
                var name = ReadToolName(node);
                var path = ReadToolPath(node);
                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                found = true;
                yield return (name, path.Trim());
            }
        }

        if (found)
        {
            yield break;
        }

        var text = ExtractText(message["content"]);
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (Match match in XmlToolPathRegex.Matches(text))
        {
            var path = match.Groups["path"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(path))
            {
                yield return (match.Groups["tag"].Value.Trim(), path);
            }
        }
    }

    private static bool IsEditTool(string name)
        => EditTools.Any(tool => tool.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool IsReadTool(string name)
        => ReadTools.Any(tool => tool.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string FormatStub(JsonObject result, bool hadPicture)
    {
        var body = hadPicture
            ? OmittedMark + "; session-created diagnostic picture was not forwarded to llama-server. The last observation pictures are still visible]"
            : OmittedMark + "; session-created diagnostic file was not forwarded to llama-server]";
        var original = ExtractText(result["content"]).TrimStart();
        if (original.StartsWith("<tool_response>", StringComparison.OrdinalIgnoreCase))
        {
            return "<tool_response>" + body + "</tool_response>";
        }

        return body;
    }

    private static bool HasImagePart(JsonNode? node)
    {
        if (node is JsonObject part
            && (IsImageType(part["type"]?.ToString())
                || part["image_url"] is not null
                || (part["source"]?["media_type"]?.ToString() ?? string.Empty)
                    .StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (node is JsonArray items)
        {
            return items.Any(HasImagePart);
        }

        return false;
    }

    private static bool IsImageType(string? type)
        => !string.IsNullOrWhiteSpace(type)
           && (type.Equals("image", StringComparison.OrdinalIgnoreCase)
               || type.Equals("image_url", StringComparison.OrdinalIgnoreCase)
               || type.Contains("image", StringComparison.OrdinalIgnoreCase));

    private static string ReadArgField(JsonNode? args, params string[] keys)
    {
        if (args is null)
        {
            return string.Empty;
        }

        if (args is JsonObject obj)
        {
            return FirstNonEmpty(keys.Select(key => obj[key]?.ToString()).ToArray());
        }

        var text = args.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        try
        {
            if (JsonNode.Parse(text) is JsonObject parsed)
            {
                return FirstNonEmpty(keys.Select(key => parsed[key]?.ToString()).ToArray());
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return string.Empty;
    }

    private static string ExtractText(JsonNode? content)
    {
        if (content is null)
        {
            return string.Empty;
        }

        if (content is JsonValue value)
        {
            return value.ToString() ?? string.Empty;
        }

        if (content is JsonArray parts)
        {
            var chunks = new List<string>();
            foreach (var part in parts.OfType<JsonObject>())
            {
                var text = part["text"]?.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    chunks.Add(text);
                }
            }

            return string.Join("\n", chunks);
        }

        return content.ToString() ?? string.Empty;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }
}

public sealed class LocalSessionArtifacts
{
    private readonly HashSet<string> _created;
    private readonly Dictionary<string, int> _edits;

    public LocalSessionArtifacts(HashSet<string> created, Dictionary<string, int> edits)
    {
        _created = created;
        _edits = edits;
        HottestPath = string.Empty;
        HottestEditCount = 0;
        CreatedCount = created.Count;
        var artifactCount = 0;
        foreach (var path in created)
        {
            if (IsArtifact(path))
            {
                artifactCount++;
            }
        }

        ArtifactCount = artifactCount;
        foreach (var pair in edits)
        {
            if (pair.Value > HottestEditCount)
            {
                HottestEditCount = pair.Value;
                HottestPath = pair.Key;
            }
        }
    }

    public int CreatedCount { get; }

    public int ArtifactCount { get; }

    public bool ShouldHalt => ArtifactCount >= LocalSessionArtifactPolicy.HaltThreshold;

    public int HottestEditCount { get; }

    public string HottestPath { get; }

    public bool IsArtifact(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var trimmed = path.Trim();
        if (LocalSessionArtifactPolicy.IsSourcePath(trimmed))
        {
            return false;
        }

        if (LocalSessionArtifactPolicy.IsDumpNamed(trimmed))
        {
            return true;
        }

        return LocalSessionArtifactPolicy.IsImagePath(trimmed) && _created.Contains(trimmed);
    }
}

public readonly record struct LocalSessionApplyResult(int Omitted, int HottestEdits, string? HottestPath, int ArtifactCount = 0);
