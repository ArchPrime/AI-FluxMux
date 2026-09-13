using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FluxMux.Avalonia.Services;

public static class LocalChatPayloadSignals
{
    private static readonly Regex XmlShellCommandRegex = new(
        @"<(?<tag>execute_command|run_command)\b[\s\S]*?<command>(?<cmd>[\s\S]*?)</command>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex XmlFileToolRegex = new(
        @"<(?<tag>read_file|write_to_file|replace_in_file|search_replace|apply_diff|apply_patch|write|write_file|create_file|read|edit|str_replace|multi_edit|list_files|search_files)\b[\s\S]*?<(?:path|file_path|target_file|target|file)>(?<path>[^<]+)</(?:path|file_path|target_file|target|file)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ImageUrlInTextRegex = new(
        @"https?://[^\s""'<>]+?\.(?:png|jpe?g|gif|webp|bmp|svg)(?:\?[^\s""'<>]*)?|data:image/|!\[[^\]]*\]\(\s*https?://|<img\b[^>]*\bsrc\s*=\s*[""']https?://|encrypted-tbn\d*\.gstatic\.com|googleusercontent\.com",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool MessageRoleCanCarryPictures(string? role)
    {
        var value = (role ?? string.Empty).Trim();
        return value.Equals("user", StringComparison.OrdinalIgnoreCase)
               || value.Equals("tool", StringComparison.OrdinalIgnoreCase);
    }

    public static bool MessageRoleMayHaveInlineImageUrls(string? role)
        => (role ?? string.Empty).Trim().Equals("user", StringComparison.OrdinalIgnoreCase);

    public static bool PayloadHasImage(JsonObject payload)
    {
        if (payload["messages"] is JsonArray messages)
        {
            foreach (var msg in messages.OfType<JsonObject>())
            {
                if (MessageHasPictures(msg))
                {
                    return true;
                }
            }
        }

        return NodeHasImage(payload["images"]);
    }

    public static bool LatestUserTurnHasImage(JsonObject payload)
    {
        if (NodeHasImage(payload["images"]))
        {
            return true;
        }

        if (payload["messages"] is not JsonArray messages)
        {
            return false;
        }

        var lastUser = LastUserMessage(payload);
        if (lastUser is not null && NodeHasImage(lastUser["content"]))
        {
            return true;
        }

        // Cline takes screenshots as tool results after the user text.
        var seenLastUser = false;
        foreach (var node in messages.OfType<JsonObject>())
        {
            if (ReferenceEquals(node, lastUser))
            {
                seenLastUser = true;
                continue;
            }

            if (!seenLastUser)
            {
                continue;
            }

            if (Str(node, "role").Equals("tool", StringComparison.OrdinalIgnoreCase)
                && NodeHasStructuredImage(node["content"]))
            {
                return true;
            }
        }

        return false;
    }

    public const int MaxForwardedImages = 1;
    public const string PictureOmittedMark = "[picture omitted]";
    public const string PictureDropNote =
        "Older pictures were not forwarded to llama-server. This Client-app chat can continue. Attach the picture in the Client app on the next message if llama-server needs that frame. The latest pictures, plus any you attach on this turn, are visible.";

    /// <summary>
    /// Drops older pictures from the forwarded pack so llama-server only sees
    /// the few most recent, plus any on the latest user turn. The Client app
    /// still has the full album.
    /// </summary>
    public static int KeepMostRecentImages(JsonObject payload, int maxImages = MaxForwardedImages)
    {
        if (payload is null || maxImages < 1)
        {
            return 0;
        }

        var kept = 0;
        var dropped = 0;
        if (payload["images"] is JsonArray topImages)
        {
            TrimImageList(topImages, maxImages, ref kept, ref dropped);
            if (topImages.Count == 0)
            {
                payload.Remove("images");
            }
        }

        var lastUser = LastRealUserMessage(payload);
        if (lastUser is not null && MessageHasPictures(lastUser))
        {
            var pinned = 0;
            var unused = 0;
            lastUser["content"] = TrimImagesFromNode(lastUser["content"], int.MaxValue, ref pinned, ref unused);
        }

        if (payload["messages"] is JsonArray messages)
        {
            for (var i = messages.Count - 1; i >= 0; i--)
            {
                if (messages[i] is not JsonObject msg || !MessageHasPictures(msg) || ReferenceEquals(msg, lastUser))
                {
                    continue;
                }

                msg["content"] = TrimImagesFromNode(msg["content"], maxImages, ref kept, ref dropped);
            }
        }

        if (dropped > 0)
        {
            AppendPictureDropNote(lastUser ?? LastUserMessage(payload));
        }

        return dropped;
    }

    public static bool StripImagesFromPayload(JsonObject payload)
    {
        var changed = false;
        if (payload["images"] is not null)
        {
            payload.Remove("images");
            changed = true;
        }

        if (payload["messages"] is not JsonArray messages)
        {
            return changed;
        }

        foreach (var msg in messages.OfType<JsonObject>())
        {
            if (!NodeHasImage(msg["content"]))
            {
                continue;
            }

            msg["content"] = StripImagesFromNode(msg["content"]);
            changed = true;
        }

        return changed;
    }

    public static bool PayloadWantsThinking(JsonObject payload)
    {
        if (payload["chat_template_kwargs"] is JsonObject kwargs
            && kwargs["enable_thinking"] is JsonValue thinkFlag
            && thinkFlag.TryGetValue<bool>(out var enabled)
            && enabled)
        {
            return true;
        }

        if (payload["enable_thinking"] is JsonValue topThink
            && topThink.TryGetValue<bool>(out var topEnabled)
            && topEnabled)
        {
            return true;
        }

        return false;
    }

    public static bool PayloadHasToolCalls(JsonObject payload)
    {
        if (payload["messages"] is not JsonArray messages)
        {
            return false;
        }

        foreach (var node in messages)
        {
            if (node is not JsonObject msg)
            {
                continue;
            }

            if (msg["tool_calls"] is JsonArray { Count: > 0 })
            {
                return true;
            }

            var role = Str(msg, "role");
            if (role.Equals("tool", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var content = msg["content"]?.ToString() ?? string.Empty;
            if (content.Contains("tool_result", StringComparison.OrdinalIgnoreCase)
                || content.Contains("<tool", StringComparison.OrdinalIgnoreCase)
                || content.Contains("[tool", StringComparison.OrdinalIgnoreCase)
                || content.Contains("tool_call", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasRepeatedToolCommand(JsonObject payload, out string command)
    {
        command = string.Empty;
        if (payload["messages"] is not JsonArray messages)
        {
            return false;
        }

        string? last = null;
        string? previous = null;
        var lastMessage = -1;
        var previousMessage = -1;
        var index = 0;
        foreach (var msg in messages.OfType<JsonObject>())
        {
            if (!Str(msg, "role").Equals("assistant", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            var foundCalls = false;
            if (msg["tool_calls"] is JsonArray calls)
            {
                foreach (var node in calls.OfType<JsonObject>())
                {
                    var fingerprint = FingerprintToolCall(node);
                    if (string.IsNullOrWhiteSpace(fingerprint))
                    {
                        continue;
                    }

                    previous = last;
                    previousMessage = lastMessage;
                    last = fingerprint;
                    lastMessage = index;
                    foundCalls = true;
                }
            }

            if (!foundCalls)
            {
                foreach (var fingerprint in FingerprintXmlToolCalls(MessageText(msg)))
                {
                    previous = last;
                    previousMessage = lastMessage;
                    last = fingerprint;
                    lastMessage = index;
                }
            }

            index++;
        }

        if (last is null
            || previous is null
            || lastMessage == previousMessage
            || !string.Equals(last, previous, StringComparison.Ordinal)
            || IsClosedLoopFingerprint(last))
        {
            return false;
        }

        command = last;
        return true;
    }

    public static IReadOnlyList<string> CollectRecentToolFingerprints(JsonObject payload, int max)
    {
        var fingerprints = new List<string>();
        if (max < 1 || payload["messages"] is not JsonArray messages)
        {
            return fingerprints;
        }

        for (var i = messages.Count - 1; i >= 0 && fingerprints.Count < max; i--)
        {
            if (messages[i] is not JsonObject msg
                || !Str(msg, "role").Equals("assistant", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var found = false;
            if (msg["tool_calls"] is JsonArray calls)
            {
                foreach (var node in calls.OfType<JsonObject>().Reverse())
                {
                    var fingerprint = FingerprintToolCall(node);
                    if (string.IsNullOrWhiteSpace(fingerprint))
                    {
                        continue;
                    }

                    fingerprints.Add(fingerprint);
                    found = true;
                    if (fingerprints.Count >= max)
                    {
                        break;
                    }
                }
            }

            if (!found)
            {
                foreach (var fingerprint in FingerprintXmlToolCalls(MessageText(msg)).Reverse())
                {
                    if (string.IsNullOrWhiteSpace(fingerprint))
                    {
                        continue;
                    }

                    fingerprints.Add(fingerprint);
                    if (fingerprints.Count >= max)
                    {
                        break;
                    }
                }
            }
        }

        return fingerprints;
    }

    public static bool RecentToolsAreClosedLoop(JsonObject payload, int lookback = 6)
    {
        var fingerprints = CollectRecentToolFingerprints(payload, lookback);

        if (fingerprints.Count == 0)
        {
            return false;
        }

        var closed = 0;
        var mutated = 0;
        foreach (var fingerprint in fingerprints)
        {
            if (IsClosedLoopFingerprint(fingerprint))
            {
                closed++;
            }

            if (IsFileMutatingFingerprint(fingerprint))
            {
                mutated++;
            }
        }

        // Screenshots next to file edits, reads, or a project run/test
        // are still that loop. Screenshot-only or echo-only is not.
        if (mutated == 0
            && !fingerprints.Any(IsClosedLoopShellFingerprint)
            && !fingerprints.Any(IsFileReadOrSearchFingerprint))
        {
            return false;
        }

        return closed * 2 > fingerprints.Count;
    }

    /// <summary>
    /// Screenshot / browser with no project file or run/test. Halt this
    /// before omit ever reaches the mill count, or the Client app has
    /// already milled. A python/test/build is useful work, not this mill.
    /// Eight looks after a file read is still discovery, not that mill.
    /// </summary>
    public static bool IsObserveOnlyFingerprint(string? fingerprint)
    {
        var text = (fingerprint ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)
            || IsFileMutatingFingerprint(text)
            || IsClosedLoopShellFingerprint(text))
        {
            return false;
        }

        var space = text.IndexOf(' ');
        var name = space < 0 ? text : text[..space];
        if (IsFileReadOrSearchTool(name))
        {
            return false;
        }

        return LocalSessionArtifactPolicy.IsObservationToolName(name);
    }

    public static int CountRecentObserveOnlyTools(JsonObject payload, int lookback)
    {
        if (lookback < 1)
        {
            return 0;
        }

        return CollectRecentToolFingerprints(payload, lookback).Count(IsObserveOnlyFingerprint);
    }

    public static int CountForwardedImages(JsonObject payload)
    {
        if (payload is null)
        {
            return 0;
        }

        var count = 0;
        if (payload["images"] is JsonArray top)
        {
            count += top.Count;
        }

        if (payload["messages"] is not JsonArray messages)
        {
            return count;
        }

        foreach (var msg in messages.OfType<JsonObject>())
        {
            if (MessageHasPictures(msg))
            {
                count += CountImagesInNode(msg["content"]);
            }
        }

        return count;
    }

    private static int CountImagesInNode(JsonNode? node)
    {
        if (node is JsonArray parts)
        {
            var n = 0;
            foreach (var part in parts.OfType<JsonObject>())
            {
                var type = part["type"]?.ToString() ?? string.Empty;
                if (type.Equals("image_url", StringComparison.OrdinalIgnoreCase)
                    || type.Equals("image", StringComparison.OrdinalIgnoreCase)
                    || part["image_url"] is not null)
                {
                    n++;
                }
            }

            return n;
        }

        return NodeHasImage(node) ? 1 : 0;
    }

    public static bool RecentToolsAreObserveOnlyMill(JsonObject payload, int minCount)
    {
        if (minCount < 1)
        {
            return false;
        }

        var fingerprints = CollectRecentToolFingerprints(payload, minCount);
        if (fingerprints.Count < minCount || !fingerprints.All(IsObserveOnlyFingerprint))
        {
            return false;
        }

        var discoveryLookback = Math.Max(minCount * 2, minCount);
        var recent = CollectRecentToolFingerprints(payload, discoveryLookback);
        if (recent.Any(IsFileReadOrSearchFingerprint))
        {
            return false;
        }

        // Eight different python/run looks are investigation. The same
        // screenshot or the same run again is the mill.
        return RecentFingerprintsAreRepeatMill(fingerprints);
    }

    /// <summary>
    /// The same command (or only two) kept coming back. Distinct tools
    /// are a normal search, not this mill.
    /// </summary>
    public static bool RecentToolsAreRepeatMill(JsonObject payload, int lookback)
    {
        var fingerprints = CollectRecentToolFingerprints(payload, lookback);
        if (fingerprints.Any(IsFileMutatingFingerprint)
            || fingerprints.Any(IsFileReadOrSearchFingerprint)
            || fingerprints.Any(IsClosedLoopShellFingerprint))
        {
            return false;
        }

        // Re-reading or running the project is investigation. The mill is
        // the same screenshot or the same echo/dir coming back.
        var millable = fingerprints
            .Where(item => !IsFileReadOrSearchFingerprint(item))
            .ToList();
        if (millable.Count < RepeatMillSameCount)
        {
            return false;
        }

        return RecentFingerprintsAreRepeatMill(millable);
    }

    /// <summary>
    /// Cline often re-reads the same one or two files after a long
    /// search, including the next chunk when a read was truncated.
    /// That is still investigation, not an echo mill.
    /// </summary>
    public static bool RecentToolsAreFileReadInvestigation(JsonObject payload, int lookback = 6)
    {
        var fingerprints = CollectRecentToolFingerprints(payload, lookback);
        if (fingerprints.Count == 0)
        {
            return false;
        }

        var reads = fingerprints.Count(IsFileReadOrSearchFingerprint);
        return reads * 2 >= fingerprints.Count;
    }

    public static bool RecentToolsAreDiverseInvestigation(JsonObject payload, int lookback)
    {
        var fingerprints = CollectRecentToolFingerprints(payload, lookback);
        if (fingerprints.Count == 0 || RecentFingerprintsAreRepeatMill(fingerprints))
        {
            return false;
        }

        var useful = fingerprints
            .Where(item => IsClosedLoopFingerprint(item) || IsFileReadOrSearchFingerprint(item))
            .ToList();
        return useful.Count * 2 >= fingerprints.Count
               && useful.Distinct(StringComparer.Ordinal).Count() >= 3;
    }

    public const int RepeatMillMaxUnique = 2;
    public const int RepeatMillSameCount = 3;

    private static bool RecentFingerprintsAreRepeatMill(IReadOnlyList<string> fingerprints)
    {
        if (fingerprints.Count < RepeatMillSameCount)
        {
            return false;
        }

        var groups = fingerprints
            .GroupBy(item => item, StringComparer.Ordinal)
            .Select(group => group.Count())
            .ToList();
        if (groups.Any(count => count >= RepeatMillSameCount))
        {
            return true;
        }

        return fingerprints.Count >= RepeatMillSameCount
               && groups.Count <= RepeatMillMaxUnique
               && fingerprints.Count >= groups.Count * RepeatMillSameCount;
    }

    public static bool IsClosedLoopFingerprint(string? fingerprint)
    {
        var text = (fingerprint ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var space = text.IndexOf(' ');
        var name = space < 0 ? text : text[..space];
        if (IsFileEditOrReadTool(name)
            || LocalSessionArtifactPolicy.IsObservationToolName(name)
            || LooksLikeFilePathFingerprint(text))
        {
            return true;
        }

        return LooksLikeClosedLoopShell(text);
    }

    public static bool IsClosedLoopShellFingerprint(string? fingerprint)
    {
        var text = (fingerprint ?? string.Empty).Trim();
        return !string.IsNullOrWhiteSpace(text)
               && !IsFileMutatingFingerprint(text)
               && LooksLikeClosedLoopShell(text);
    }

    /// <summary>
    /// A file write or patch. Screenshots, reads, and python/test runs do
    /// not count — those can repeat while the project files stay the same.
    /// </summary>
    public static bool IsFileMutatingFingerprint(string? fingerprint)
    {
        var text = (fingerprint ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text) || LooksLikeClosedLoopShell(text))
        {
            return false;
        }

        var space = text.IndexOf(' ');
        var name = space < 0 ? text : text[..space];
        if (IsFileReadOrSearchTool(name)
            || LocalSessionArtifactPolicy.IsObservationToolName(name))
        {
            return false;
        }

        if (IsFileMutatingTool(name))
        {
            return true;
        }

        if (!LooksLikeFilePathFingerprint(text))
        {
            return false;
        }

        var path = text[(space + 1)..];
        return LocalSessionArtifactPolicy.IsSourcePath(path)
            && !LocalSessionArtifactPolicy.IsImagePath(path);
    }

    // "edit C:\...\car_racing.py" is a debug loop even when the Client app
    // uses a tool name that is not on the Cline list.
    private static bool LooksLikeFilePathFingerprint(string fingerprint)
    {
        var space = fingerprint.IndexOf(' ');
        if (space < 0)
        {
            return false;
        }

        var path = fingerprint[(space + 1)..];
        return path.Contains('\\') || path.Contains('/');
    }

    private static bool IsFileEditOrReadTool(string name)
        => IsFileMutatingTool(name) || IsFileReadOrSearchTool(name);

    private static bool IsFileMutatingTool(string name)
        => name.Equals("write", StringComparison.OrdinalIgnoreCase)
           || name.Equals("write_to_file", StringComparison.OrdinalIgnoreCase)
           || name.Equals("write_file", StringComparison.OrdinalIgnoreCase)
           || name.Equals("create_file", StringComparison.OrdinalIgnoreCase)
           || name.Equals("edit", StringComparison.OrdinalIgnoreCase)
           || name.Equals("replace_in_file", StringComparison.OrdinalIgnoreCase)
           || name.Equals("search_replace", StringComparison.OrdinalIgnoreCase)
           || name.Equals("str_replace", StringComparison.OrdinalIgnoreCase)
           || name.Equals("multi_edit", StringComparison.OrdinalIgnoreCase)
           || name.Equals("apply_diff", StringComparison.OrdinalIgnoreCase)
           || name.Equals("apply_patch", StringComparison.OrdinalIgnoreCase);

    private static bool IsFileReadOrSearchFingerprint(string? fingerprint)
    {
        var text = (fingerprint ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var space = text.IndexOf(' ');
        var name = space < 0 ? text : text[..space];
        return IsFileReadOrSearchTool(name);
    }

    private static bool IsFileReadOrSearchTool(string name)
        => name.Equals("read", StringComparison.OrdinalIgnoreCase)
           || name.Equals("read_file", StringComparison.OrdinalIgnoreCase)
           || name.Equals("readFile", StringComparison.OrdinalIgnoreCase)
           || name.Equals("readAll", StringComparison.OrdinalIgnoreCase)
           || name.Equals("readRelated", StringComparison.OrdinalIgnoreCase)
           || name.Equals("readBytes", StringComparison.OrdinalIgnoreCase)
           || name.Equals("list_files", StringComparison.OrdinalIgnoreCase)
           || name.Equals("list_dir", StringComparison.OrdinalIgnoreCase)
           || name.Equals("glob", StringComparison.OrdinalIgnoreCase)
           || name.Equals("search_files", StringComparison.OrdinalIgnoreCase)
           || name.Equals("grep", StringComparison.OrdinalIgnoreCase)
           || name.Equals("codebase_search", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeClosedLoopShell(string command)
        => command.Contains("test", StringComparison.OrdinalIgnoreCase)
           || command.Contains("pytest", StringComparison.OrdinalIgnoreCase)
           || command.Contains("jest", StringComparison.OrdinalIgnoreCase)
           || command.Contains("vitest", StringComparison.OrdinalIgnoreCase)
           || command.Contains("playwright", StringComparison.OrdinalIgnoreCase)
           || command.Contains("cypress", StringComparison.OrdinalIgnoreCase)
           || command.Contains("spec", StringComparison.OrdinalIgnoreCase)
           || command.Contains("lint", StringComparison.OrdinalIgnoreCase)
           || command.Contains("build", StringComparison.OrdinalIgnoreCase)
           || command.Contains("msbuild", StringComparison.OrdinalIgnoreCase)
           || command.Contains("python", StringComparison.OrdinalIgnoreCase)
           || command.Contains("dotnet run", StringComparison.OrdinalIgnoreCase)
           || command.Contains("npm start", StringComparison.OrdinalIgnoreCase)
           || command.Contains("npm run", StringComparison.OrdinalIgnoreCase);

    public static bool PayloadLooksLikePatchOrPlan(JsonObject payload)
    {
        var user = LastUserText(payload).ToLowerInvariant();
        if (user.Length == 0)
        {
            return false;
        }

        return user.Contains("```diff")
            || user.Contains("@@ ")
            || user.Contains("run_command")
            || user.Contains("apply_patch")
            || user.Contains("write_to_file")
            || user.Contains("replace_in_file")
            || user.Contains("search_replace")
            || user.Contains("--- a/")
            || user.Contains("+++ b/");
    }

    public static string LastUserText(JsonObject payload)
    {
        var lastUser = LastUserMessage(payload);
        if (lastUser is not null)
        {
            return lastUser["content"]?.ToString() ?? string.Empty;
        }

        return payload["prompt"]?.ToString() ?? string.Empty;
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

            if (!Str(msg, "role").Equals("user", StringComparison.OrdinalIgnoreCase))
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

    private static void AppendPictureDropNote(JsonObject? message)
    {
        if (message is null)
        {
            return;
        }

        var note = PictureDropNote;
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

    private static JsonObject? LastUserMessage(JsonObject payload)
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

            if (Str(msg, "role").Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                return msg;
            }
        }

        return null;
    }

    private static void TrimImageList(JsonArray images, int maxImages, ref int kept, ref int dropped)
    {
        var keptItems = new JsonNode?[images.Count];
        for (var i = images.Count - 1; i >= 0; i--)
        {
            var item = images[i];
            if (item is not null && NodeHasImage(item))
            {
                if (kept < maxImages)
                {
                    kept++;
                    keptItems[i] = item.DeepClone();
                }
                else
                {
                    dropped++;
                }
            }
            else
            {
                keptItems[i] = item?.DeepClone();
            }
        }

        images.Clear();
        foreach (var item in keptItems)
        {
            if (item is not null)
            {
                images.Add(item);
            }
        }
    }

    private static JsonNode? TrimImagesFromNode(JsonNode? node, int maxImages, ref int kept, ref int dropped)
    {
        if (node is JsonArray items)
        {
            var keptParts = new JsonNode?[items.Count];
            for (var i = items.Count - 1; i >= 0; i--)
            {
                var item = items[i];
                if (item is JsonObject part && PartIsImage(part))
                {
                    if (kept < maxImages)
                    {
                        kept++;
                        keptParts[i] = part.DeepClone();
                    }
                    else
                    {
                        dropped++;
                        keptParts[i] = new JsonObject { ["type"] = "text", ["text"] = PictureOmittedMark };
                    }
                }
                else
                {
                    keptParts[i] = TrimImagesFromNode(item, maxImages, ref kept, ref dropped);
                }
            }

            var result = new JsonArray();
            foreach (var part in keptParts)
            {
                if (part is not null)
                {
                    result.Add(part);
                }
            }

            if (result.Count == 0)
            {
                result.Add(new JsonObject { ["type"] = "text", ["text"] = PictureOmittedMark });
            }

            return result;
        }

        if (node is JsonObject obj && PartIsImage(obj))
        {
            if (kept < maxImages)
            {
                kept++;
                return obj.DeepClone();
            }

            dropped++;
            return "[picture omitted]";
        }

        if (node is JsonValue value)
        {
            var text = value.ToString() ?? string.Empty;
            if (!ImageUrlInTextRegex.IsMatch(text))
            {
                return node;
            }

            var matches = ImageUrlInTextRegex.Matches(text);
            if (kept + matches.Count <= maxImages)
            {
                kept += matches.Count;
                return node;
            }

            var dropCount = matches.Count - Math.Max(0, maxImages - kept);
            var trimmed = text;
            for (var i = 0; i < dropCount; i++)
            {
                trimmed = ImageUrlInTextRegex.Replace(trimmed, "[picture omitted]", 1);
                dropped++;
            }

            kept += matches.Count - dropCount;
            return trimmed;
        }

        return node?.DeepClone();
    }

    private static JsonNode? StripImagesFromNode(JsonNode? node)
    {
        if (node is JsonArray items)
        {
            var kept = new JsonArray();
            foreach (var item in items)
            {
                if (item is JsonObject part && PartIsImage(part))
                {
                    continue;
                }

                kept.Add(item?.DeepClone());
            }

            if (kept.Count == 0)
            {
                kept.Add(new JsonObject { ["type"] = "text", ["text"] = "[picture omitted]" });
            }

            return kept;
        }

        if (node is JsonObject obj && PartIsImage(obj))
        {
            return "[picture omitted]";
        }

        if (node is JsonValue value)
        {
            var text = value.ToString() ?? string.Empty;
            if (ImageUrlInTextRegex.IsMatch(text))
            {
                return ImageUrlInTextRegex.Replace(text, "[picture omitted]");
            }
        }

        return node?.DeepClone();
    }

    private static string FingerprintToolCall(JsonObject call)
    {
        var name = (call["function"]?["name"]?.ToString()
            ?? call["name"]?.ToString()
            ?? string.Empty).Trim();
        var argsNode = call["function"]?["arguments"] ?? call["arguments"];
        if (IsShellTool(name))
        {
            var command = ReadShellCommand(argsNode);
            if (!string.IsNullOrWhiteSpace(command))
            {
                return CollapseWhitespace(command);
            }
        }

        var path = FirstNonEmpty(
            ReadJsonArg(argsNode, "path"),
            ReadJsonArg(argsNode, "file_path"),
            ReadJsonArg(argsNode, "file"),
            ReadJsonArg(argsNode, "target"),
            ReadJsonArg(argsNode, "target_file"));
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(path))
        {
            return CollapseWhitespace(name + " " + path);
        }

        if (LocalSessionArtifactPolicy.IsObservationToolName(name))
        {
            return name;
        }

        var args = argsNode?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(args) || args == "{}")
        {
            return string.Empty;
        }

        return CollapseWhitespace(name + " " + args);
    }

    public static IEnumerable<string> ListXmlShellCommands(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (Match match in XmlShellCommandRegex.Matches(text))
        {
            var command = CollapseWhitespace(match.Groups["cmd"].Value);
            if (!string.IsNullOrWhiteSpace(command))
            {
                yield return command;
            }
        }
    }

    private static IEnumerable<string> FingerprintXmlToolCalls(string text)
    {
        foreach (var command in ListXmlShellCommands(text))
        {
            yield return command;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (Match match in XmlFileToolRegex.Matches(text))
        {
            var name = match.Groups["tag"].Value.Trim();
            var path = CollapseWhitespace(match.Groups["path"].Value);
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(path))
            {
                yield return CollapseWhitespace(name + " " + path);
            }
        }
    }

    private static string ReadJsonArg(JsonNode? args, string key)
    {
        if (args is JsonObject obj)
        {
            return obj[key]?.ToString() ?? string.Empty;
        }

        var text = args?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        try
        {
            if (JsonNode.Parse(text) is JsonObject parsed)
            {
                return parsed[key]?.ToString() ?? string.Empty;
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return string.Empty;
    }

    private static bool IsShellTool(string name)
    {
        var value = (name ?? string.Empty).Trim();
        return value.Equals("execute_command", StringComparison.OrdinalIgnoreCase)
            || value.Equals("run_command", StringComparison.OrdinalIgnoreCase)
            || value.Equals("shell", StringComparison.OrdinalIgnoreCase)
            || value.Equals("bash", StringComparison.OrdinalIgnoreCase)
            || value.Equals("command", StringComparison.OrdinalIgnoreCase)
            || value.Equals("run_terminal_cmd", StringComparison.OrdinalIgnoreCase)
            || value.Equals("terminal", StringComparison.OrdinalIgnoreCase)
            || value.Equals("powershell", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadShellCommand(JsonNode? args)
    {
        if (args is null)
        {
            return string.Empty;
        }

        if (args is JsonObject obj)
        {
            return FirstNonEmpty(
                obj["command"]?.ToString(),
                obj["cmd"]?.ToString(),
                obj["input"]?.ToString());
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
                return FirstNonEmpty(
                    parsed["command"]?.ToString(),
                    parsed["cmd"]?.ToString(),
                    parsed["input"]?.ToString());
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return string.Empty;
    }

    private static string MessageText(JsonObject msg)
    {
        var content = msg["content"];
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

            if (chunks.Count > 0)
            {
                return string.Join("\n", chunks);
            }
        }

        return content?.ToString() ?? string.Empty;
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

    private static string CollapseWhitespace(string text)
        => string.Join(" ", (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool MessageHasPictures(JsonObject msg)
    {
        var role = Str(msg, "role");
        if (!MessageRoleCanCarryPictures(role))
        {
            return false;
        }

        return MessageRoleMayHaveInlineImageUrls(role)
            ? NodeHasImage(msg["content"])
            : NodeHasStructuredImage(msg["content"]);
    }

    private static bool NodeHasStructuredImage(JsonNode? node)
    {
        if (node is JsonObject part && PartIsImage(part))
        {
            return true;
        }

        if (node is JsonArray items)
        {
            foreach (var item in items)
            {
                if (NodeHasStructuredImage(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool PartIsImage(JsonObject part)
    {
        var type = Str(part, "type").ToLowerInvariant();
        if (type.Contains("image")
            || part["image_url"] is not null
            || part["image"] is not null
            || part["input_image"] is not null)
        {
            return true;
        }

        if (part["source"] is JsonObject source)
        {
            var media = Str(source, "media_type");
            if (string.IsNullOrEmpty(media))
            {
                media = Str(source, "mediaType");
            }

            return media.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool NodeHasImage(JsonNode? node)
    {
        if (node is null)
        {
            return false;
        }

        if (node is JsonValue value)
        {
            var text = value.ToString() ?? string.Empty;
            return ImageUrlInTextRegex.IsMatch(text);
        }

        if (node is JsonObject part)
        {
            var type = Str(part, "type").ToLowerInvariant();
            if (type.Contains("image") || part["image_url"] is not null || part["image"] is not null || part["input_image"] is not null)
            {
                return true;
            }

            if (part["source"] is JsonObject source
                && (source["data"] is not null
                    || source["url"] is not null
                    || Str(source, "media_type").Contains("image", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (NodeHasImage(part["image_url"]) || NodeHasImage(part["url"]) || NodeHasImage(part["text"]) || NodeHasImage(part["content"]) || NodeHasImage(part["source"]))
            {
                return true;
            }
        }

        if (node is JsonArray items)
        {
            foreach (var item in items)
            {
                if (NodeHasImage(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string Str(JsonObject obj, string key, string fallback = "")
        => obj[key]?.ToString() ?? fallback;
}
