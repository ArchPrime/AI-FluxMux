using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Generic Client-app writer: merge tokens into an existing JSON file.
/// Add a fluxmux_config.json EndpointAdapters object with kind json-merge.
/// </summary>
public sealed class JsonMergeEndpointAdapter(EndpointAdapterDefinition definition) : IEndpointSettingsAdapter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonWrite = new() { WriteIndented = true };

    public string Id { get; } = definition.Id;

    public EndpointSettingsSyncResult Apply(EndpointSettingsSnapshot snapshot)
    {
        var path = ExpandPath(definition.Path);
        if (string.IsNullOrWhiteSpace(path))
        {
            return new EndpointSettingsSyncResult(Id, false, false, false, Id + " has no path.");
        }

        if (!File.Exists(path))
        {
            if (definition.OnlyIfFileExists)
            {
                return new EndpointSettingsSyncResult(
                    Id,
                    true,
                    false,
                    true,
                    Id + " settings file is not on this PC yet.");
            }

            return new EndpointSettingsSyncResult(Id, false, false, false, Id + " settings file was not found: " + path);
        }

        JsonObject root;
        try
        {
            var parsed = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (parsed is null)
            {
                return new EndpointSettingsSyncResult(Id, false, false, false, Id + " settings file is not a JSON object.");
            }

            root = parsed;
        }
        catch (Exception ex)
        {
            return new EndpointSettingsSyncResult(Id, false, false, false, "Could not read " + Id + ": " + ex.Message);
        }

        if (!string.IsNullOrWhiteSpace(definition.BaseUrlPath))
        {
            var baseUrl = ReadDotted(root, definition.BaseUrlPath);
            if (!ClineContextSync.PointsAtFluxMuxPort(baseUrl, snapshot.Port))
            {
                return new EndpointSettingsSyncResult(
                    Id,
                    true,
                    false,
                    true,
                    Id + " is not pointed at this AI-FluxMux Port.");
            }
        }

        var before = root.ToJsonString();
        foreach (var pair in definition.Set)
        {
            WriteDotted(root, pair.Key, Substitute(pair.Value, snapshot));
        }

        if (string.Equals(before, root.ToJsonString(), StringComparison.Ordinal))
        {
            return new EndpointSettingsSyncResult(Id, true, false, false, Id + " already matches this local model profile.");
        }

        try
        {
            File.WriteAllText(path + ".bak", File.ReadAllText(path), Utf8NoBom);
            File.WriteAllText(path, root.ToJsonString(JsonWrite) + Environment.NewLine, Utf8NoBom);
        }
        catch (Exception ex)
        {
            return new EndpointSettingsSyncResult(Id, false, false, false, "Could not write " + Id + ": " + ex.Message);
        }

        return new EndpointSettingsSyncResult(
            Id,
            true,
            true,
            false,
            Id + " was updated to match this local model profile.");
    }

    internal static string ExpandPath(string path)
    {
        var text = (path ?? string.Empty).Trim();
        if (text.StartsWith("~/", StringComparison.Ordinal) || text.StartsWith("~\\", StringComparison.Ordinal))
        {
            text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), text[2..]);
        }

        return Environment.ExpandEnvironmentVariables(text);
    }

    internal static JsonNode? Substitute(string template, EndpointSettingsSnapshot snapshot)
    {
        var text = template ?? string.Empty;
        if (text.Equals("{port}", StringComparison.OrdinalIgnoreCase))
        {
            return JsonValue.Create(snapshot.Port);
        }

        if (text.Equals("{context}", StringComparison.OrdinalIgnoreCase))
        {
            return JsonValue.Create(snapshot.LoadedContextWindow);
        }

        if (text.Equals("{advertisedContext}", StringComparison.OrdinalIgnoreCase))
        {
            return JsonValue.Create(snapshot.AdvertisedContextWindow);
        }

        if (text.Equals("{maxTokens}", StringComparison.OrdinalIgnoreCase))
        {
            return JsonValue.Create(snapshot.AdvertisedMaxTokens);
        }

        if (text.Equals("{imagesOn}", StringComparison.OrdinalIgnoreCase))
        {
            return JsonValue.Create(snapshot.ImagesOn);
        }

        if (text.Equals("{reasoningOn}", StringComparison.OrdinalIgnoreCase))
        {
            return JsonValue.Create(snapshot.ReasoningOn);
        }

        if (text.Equals("{displayName}", StringComparison.OrdinalIgnoreCase))
        {
            return JsonValue.Create(snapshot.DisplayName);
        }

        if (text.Equals("{modelId}", StringComparison.OrdinalIgnoreCase))
        {
            return JsonValue.Create(
                string.IsNullOrWhiteSpace(snapshot.ModelId)
                    ? FluxMuxGatewayModels.LocalModelId
                    : snapshot.ModelId);
        }

        return JsonValue.Create(text
            .Replace("{port}", snapshot.Port.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{context}", snapshot.LoadedContextWindow.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{advertisedContext}", snapshot.AdvertisedContextWindow.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{maxTokens}", snapshot.AdvertisedMaxTokens.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{imagesOn}", snapshot.ImagesOn ? "true" : "false", StringComparison.OrdinalIgnoreCase)
            .Replace("{reasoningOn}", snapshot.ReasoningOn ? "true" : "false", StringComparison.OrdinalIgnoreCase)
            .Replace("{displayName}", snapshot.DisplayName, StringComparison.OrdinalIgnoreCase)
            .Replace(
                "{modelId}",
                string.IsNullOrWhiteSpace(snapshot.ModelId) ? FluxMuxGatewayModels.LocalModelId : snapshot.ModelId,
                StringComparison.OrdinalIgnoreCase));
    }

    internal static string ReadDotted(JsonObject root, string path)
    {
        var node = Walk(root, path, create: false);
        return node?.ToString()?.Trim() ?? string.Empty;
    }

    internal static void WriteDotted(JsonObject root, string path, JsonNode? value)
    {
        var parts = SplitPath(path);
        if (parts.Length == 0)
        {
            return;
        }

        JsonObject current = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (current[parts[i]] is not JsonObject next)
            {
                next = new JsonObject();
                current[parts[i]] = next;
            }

            current = next;
        }

        current[parts[^1]] = value is null ? null : value.DeepClone();
    }

    private static JsonNode? Walk(JsonObject root, string path, bool create)
    {
        JsonNode? node = root;
        foreach (var part in SplitPath(path))
        {
            if (node is not JsonObject obj)
            {
                return null;
            }

            if (obj[part] is null)
            {
                if (!create)
                {
                    return null;
                }

                obj[part] = new JsonObject();
            }

            node = obj[part];
        }

        return node;
    }

    private static string[] SplitPath(string path)
        => (path ?? string.Empty)
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
