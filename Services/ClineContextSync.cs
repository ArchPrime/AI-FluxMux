using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

public readonly record struct ClineContextSyncOptions(
    int FluxMuxPort,
    int ContextWindow,
    string ModelId,
    bool ImagesOn,
    string DisplayName,
    int MaxTokens)
{
    public static ClineContextSyncOptions Create(
        int fluxMuxPort,
        int contextWindow,
        string? modelId = null,
        bool imagesOn = false,
        string? displayName = null,
        int maxTokens = 0)
    {
        var port = Math.Clamp(fluxMuxPort, 1, 65535);
        var context = Math.Clamp(contextWindow, 1024, DeepSeekHarnessSetup.MaxLocalContextWindow);
        var id = string.IsNullOrWhiteSpace(modelId) ? FluxMuxGatewayModels.LocalModelId : modelId.Trim();
        var label = string.IsNullOrWhiteSpace(displayName) ? id : displayName.Trim();
        var replyBudget = maxTokens > 0 ? Math.Clamp(maxTokens, 1, context) : 0;
        return new ClineContextSyncOptions(port, context, id, imagesOn, label, replyBudget);
    }
}

public readonly record struct ClineContextSyncResult(
    bool Success,
    bool Changed,
    bool Skipped,
    string ModelsPath,
    string Message);

/// <summary>What the Servers tab can say about Cline's own settings on this PC.</summary>
public enum ClinePortStatus
{
    /// <summary>No Cline settings file was found, so Cline has probably not run here.</summary>
    NotFound,

    /// <summary>Cline has settings, but its OpenAI Compatible base URL is not Port.</summary>
    PointsElsewhere,

    /// <summary>Cline calls Port, so Launch can keep its Context and profile name in step.</summary>
    PointsAtPort
}

/// <summary>
/// Cline 4.x OpenAI Compatible stores Context in ~/.cline/data/settings/models.json.
/// It defaults to 128000 and does not read Port. This writes the loaded local Context
/// onto model id local when that provider already points at AI-FluxMux Port.
/// It also writes <c>maxTokens</c> from the loaded reply budget (llama-server
/// <c>-n</c>), not Context, so Cline does not keep an 8k OpenAI default and
/// report a full output limit while the window is half empty.
/// It also turns off Cline Auto compact (useAutoCondense) in globalState.json so
/// FluxMux Compact is the one that shortens forwarded history, and sets Supports
/// Images from the loaded model profile (not a Cline-side VRAM switch).
/// Older Cline is not updated here (editor Model Configuration). Do not treat this
/// as a generic Client-app settings mapper.
/// </summary>
public static class ClineContextSync
{
    public const string ProviderId = "openai-compatible";
    public const int DefaultContextWindow = 128000;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonWrite = new() { WriteIndented = true };

    public static string ResolveClineHomeDirectory()
    {
        var overrideDir = Environment.GetEnvironmentVariable("CLINE_DIR")?.Trim();
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            return overrideDir;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cline");
    }

    public static string ResolveDataDirectory()
    {
        var overrideDir = Environment.GetEnvironmentVariable("CLINE_DATA_DIR")?.Trim();
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            return overrideDir;
        }

        return Path.Combine(ResolveClineHomeDirectory(), "data");
    }

    public static string ResolveSettingsDirectory()
        => Path.Combine(ResolveDataDirectory(), "settings");

    public static string ResolveModelsPath()
        => Path.Combine(ResolveSettingsDirectory(), "models.json");

    public static string ResolveProvidersPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("CLINE_PROVIDER_SETTINGS_PATH")?.Trim();
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        return Path.Combine(ResolveSettingsDirectory(), "providers.json");
    }

    public static string ResolveGlobalStatePath()
        => Path.Combine(ResolveDataDirectory(), "globalState.json");

    public static string ResolveGlobalSettingsPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("CLINE_GLOBAL_SETTINGS_PATH")?.Trim();
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        return Path.Combine(ResolveSettingsDirectory(), "global-settings.json");
    }

    public static bool PointsAtFluxMuxPort(string? baseUrl, int fluxMuxPort)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return false;
        }

        var text = baseUrl.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && !Uri.TryCreate("http://" + text, UriKind.Absolute, out uri))
        {
            return false;
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = uri.Host;
        if (!host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var port = uri.IsDefaultPort ? 80 : uri.Port;
        if (port != fluxMuxPort)
        {
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        return path.Length == 0
            || path.Equals("/v1", StringComparison.OrdinalIgnoreCase);
    }

    public static ClineContextSyncResult MergeIntoModelsFile(ClineContextSyncOptions options)
        => MergeIntoModelsFile(
            options,
            ResolveModelsPath(),
            ResolveProvidersPath(),
            ResolveGlobalStatePath(),
            ResolveGlobalSettingsPath());

    public static ClineContextSyncResult MergeIntoModelsFile(
        ClineContextSyncOptions options,
        string modelsPath,
        string providersPath,
        string? globalStatePath = null,
        string? globalSettingsPath = null)
    {
        if (string.IsNullOrWhiteSpace(modelsPath))
        {
            return new ClineContextSyncResult(
                false,
                false,
                false,
                modelsPath ?? string.Empty,
                "Could not resolve Cline models.json.");
        }

        try
        {
            var modelsExists = File.Exists(modelsPath);
            var providersExists = File.Exists(providersPath);
            if (!modelsExists && !providersExists)
            {
                return new ClineContextSyncResult(
                    true,
                    false,
                    true,
                    modelsPath,
                    "Cline 4.x has no OpenAI Compatible settings file yet.");
            }

            JsonObject? providersRoot = null;
            if (providersExists)
            {
                if (!TryReadJsonObject(providersPath, out providersRoot, out var providersError))
                {
                    return Fail(modelsPath, providersError);
                }
            }

            var providerSettings = ReadOpenAiCompatibleSettings(providersRoot);
            var providersBaseUrl = Str(providerSettings, "baseUrl");
            var visibleId = VisibleModelId(options);

            JsonObject modelsRoot;
            if (modelsExists)
            {
                if (!TryReadJsonObject(modelsPath, out var parsed, out var modelsError) || parsed is null)
                {
                    return Fail(modelsPath, modelsError);
                }

                modelsRoot = parsed;
            }
            else
            {
                modelsRoot = new JsonObject { ["version"] = 1, ["providers"] = new JsonObject() };
            }

            var providersMap = modelsRoot["providers"] as JsonObject;
            if (providersMap is null)
            {
                providersMap = new JsonObject();
                modelsRoot["providers"] = providersMap;
            }

            var block = providersMap[ProviderId] as JsonObject;
            var modelsBaseUrl = Str(block?["provider"] as JsonObject, "baseUrl");
            var pointsAtPort = PointsAtFluxMuxPort(providersBaseUrl, options.FluxMuxPort)
                || PointsAtFluxMuxPort(modelsBaseUrl, options.FluxMuxPort);
            if (!pointsAtPort)
            {
                return new ClineContextSyncResult(
                    true,
                    false,
                    true,
                    modelsPath,
                    "Cline 4.x OpenAI Compatible is not pointed at this AI-FluxMux Port.");
            }

            var nextBlock = BuildProviderBlock(block, options, visibleId);
            var modelsChanged = !JsonNode.DeepEquals(block, nextBlock);
            if (modelsChanged)
            {
                providersMap[ProviderId] = nextBlock;
                if (modelsRoot["version"] is null)
                {
                    modelsRoot["version"] = 1;
                }

                WriteJsonAtomic(modelsPath, modelsRoot, backupExisting: modelsExists);
            }

            var providersChanged = PatchProvidersSelectedModel(providersPath, providersRoot, visibleId);
            var stateChanged = PatchGlobalState(
                globalStatePath,
                options.ContextWindow,
                options.ImagesOn,
                options.DisplayName,
                options.MaxTokens);
            var settingsChanged = PatchGlobalSettingsAutoCompactOff(globalSettingsPath);
            var changed = modelsChanged || providersChanged || stateChanged || settingsChanged;
            return new ClineContextSyncResult(
                true,
                changed,
                false,
                modelsPath,
                BuildSyncMessage(options.ContextWindow, modelsChanged, stateChanged || settingsChanged));
        }
        catch (Exception ex)
        {
            return Fail(modelsPath, "Could not write Cline models.json: " + ex.Message);
        }
    }

    internal static JsonObject BuildProviderBlock(JsonObject? existing, ClineContextSyncOptions options, string modelId)
    {
        var block = existing is null
            ? new JsonObject()
            : (JsonObject)existing.DeepClone();

        var provider = block["provider"] as JsonObject;
        if (provider is null)
        {
            provider = new JsonObject();
            block["provider"] = provider;
        }

        if (string.IsNullOrWhiteSpace(Str(provider, "name")))
        {
            provider["name"] = "OpenAI Compatible";
        }

        if (!PointsAtFluxMuxPort(Str(provider, "baseUrl"), options.FluxMuxPort))
        {
            provider["baseUrl"] = "http://127.0.0.1:" + options.FluxMuxPort.ToString(CultureInfo.InvariantCulture);
        }

        provider["defaultModelId"] = modelId;

        var models = block["models"] as JsonObject;
        if (models is null)
        {
            models = new JsonObject();
            block["models"] = models;
        }

        WriteModelEntry(models, FluxMuxGatewayModels.LocalModelId, options);
        WriteModelEntry(models, modelId, options);
        return block;
    }

    private static void WriteModelEntry(JsonObject models, string modelId, ClineContextSyncOptions options)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return;
        }

        var entry = models[modelId] as JsonObject;
        if (entry is null)
        {
            entry = new JsonObject();
            models[modelId] = entry;
        }

        entry["name"] = string.IsNullOrWhiteSpace(options.DisplayName) ? modelId : options.DisplayName;
        entry["contextWindow"] = options.ContextWindow;
        entry["maxInputTokens"] = options.ContextWindow;
        ApplyMaxTokens(entry, options.MaxTokens);
        ApplyImagesFlags(entry, options.ImagesOn);
    }

    private static string VisibleModelId(ClineContextSyncOptions options)
        => string.IsNullOrWhiteSpace(options.DisplayName)
            ? options.ModelId
            : options.DisplayName;

    internal static bool ApplyMaxTokens(JsonObject entry, int maxTokens)
    {
        if (maxTokens <= 0)
        {
            return false;
        }

        if (TryReadPositiveInt(entry["maxTokens"], out var current) && current == maxTokens)
        {
            return false;
        }

        entry["maxTokens"] = maxTokens;
        return true;
    }

    internal static void ApplyImagesFlags(JsonObject entry, bool imagesOn)
    {
        entry["supportsVision"] = imagesOn;
        var capabilities = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (entry["capabilities"] is JsonArray existing)
        {
            foreach (var node in existing)
            {
                var name = node?.ToString()?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name)
                    || name.Equals("images", StringComparison.OrdinalIgnoreCase)
                    || !seen.Add(name))
                {
                    continue;
                }

                capabilities.Add(name);
            }
        }

        if (capabilities.Count == 0)
        {
            capabilities.Add("streaming");
            capabilities.Add("tools");
        }

        if (imagesOn)
        {
            capabilities.Add("images");
        }

        entry["capabilities"] = capabilities;
    }

    internal static bool ApplyAutoCondenseOff(JsonObject root)
    {
        if (root["useAutoCondense"] is JsonValue existing
            && existing.TryGetValue<bool>(out var on)
            && !on)
        {
            return false;
        }

        root["useAutoCondense"] = false;
        return true;
    }

    internal static bool ApplyOpenAiModelInfo(
        JsonObject root,
        int contextWindow,
        bool imagesOn,
        string displayName,
        int maxTokens = 0)
    {
        var changed = false;
        var label = string.IsNullOrWhiteSpace(displayName) ? FluxMuxGatewayModels.LocalModelId : displayName.Trim();
        foreach (var key in new[] { "planModeOpenAiModelInfo", "actModeOpenAiModelInfo", "openAiModelInfo" })
        {
            if (root[key] is not JsonObject info)
            {
                continue;
            }

            if (!TryReadPositiveInt(info["contextWindow"], out var current) || current != contextWindow)
            {
                info["contextWindow"] = contextWindow;
                changed = true;
            }

            if (!TryReadPositiveInt(info["maxInputTokens"], out var maxInput) || maxInput != contextWindow)
            {
                info["maxInputTokens"] = contextWindow;
                changed = true;
            }

            if (ApplyMaxTokens(info, maxTokens))
            {
                changed = true;
            }

            if (info["supportsImages"] is not JsonValue imagesValue
                || !imagesValue.TryGetValue<bool>(out var supportsImages)
                || supportsImages != imagesOn)
            {
                info["supportsImages"] = imagesOn;
                changed = true;
            }

            if (!string.Equals(Str(info, "name"), label, StringComparison.Ordinal))
            {
                info["name"] = label;
                changed = true;
            }
        }

        foreach (var key in new[] { "planModeOpenAiModelId", "actModeOpenAiModelId", "openAiModelId" })
        {
            if (root[key] is null)
            {
                continue;
            }

            if (!string.Equals(Str(root, key), label, StringComparison.Ordinal))
            {
                root[key] = label;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Whether Cline has settings on this PC, and whether they call Port. Pointing at
    /// Port is the precondition for Launch writing Context and the profile name, so the
    /// Servers tab says which of the three it is rather than leaving it to a failed turn.
    /// </summary>
    public static ClinePortStatus ReadPortStatus(int fluxMuxPort)
        => ReadPortStatus(
            fluxMuxPort,
            ResolveModelsPath(),
            ResolveProvidersPath(),
            ResolveGlobalStatePath());

    public static ClinePortStatus ReadPortStatus(
        int fluxMuxPort,
        string modelsPath,
        string providersPath,
        string? globalStatePath = null)
    {
        // A settings file that exists but will not parse still means Cline is installed
        // here, so presence is judged on the file rather than on reading it.
        var found = false;
        try
        {
            if (File.Exists(providersPath))
            {
                found = true;
                if (TryReadJsonObject(providersPath, out var providersRoot, out _)
                    && ReadOpenAiCompatibleSettings(providersRoot) is { } settings
                    && PointsAtFluxMuxPort(Str(settings, "baseUrl"), fluxMuxPort))
                {
                    return ClinePortStatus.PointsAtPort;
                }
            }

            if (File.Exists(modelsPath))
            {
                found = true;
                if (TryReadJsonObject(modelsPath, out var modelsRoot, out _)
                    && (modelsRoot?["providers"] as JsonObject)?[ProviderId] is JsonObject block
                    && PointsAtFluxMuxPort(Str(block["provider"] as JsonObject, "baseUrl"), fluxMuxPort))
                {
                    return ClinePortStatus.PointsAtPort;
                }
            }

            if (!string.IsNullOrWhiteSpace(globalStatePath) && File.Exists(globalStatePath))
            {
                found = true;
                if (TryReadJsonObject(globalStatePath, out var stateRoot, out _)
                    && stateRoot is not null
                    && PointsAtFluxMuxPort(Str(stateRoot, "openAiBaseUrl"), fluxMuxPort))
                {
                    return ClinePortStatus.PointsAtPort;
                }
            }
        }
        catch (Exception)
        {
            // A status line must never take the tab down with it.
            return found ? ClinePortStatus.PointsElsewhere : ClinePortStatus.NotFound;
        }

        return found ? ClinePortStatus.PointsElsewhere : ClinePortStatus.NotFound;
    }

    public static IReadOnlyList<string> ReadAdvertisedModelIds(int fluxMuxPort)
        => ReadAdvertisedModelIds(
            fluxMuxPort,
            ResolveModelsPath(),
            ResolveProvidersPath(),
            ResolveGlobalStatePath());

    public static IReadOnlyList<string> ReadAdvertisedModelIds(
        int fluxMuxPort,
        string modelsPath,
        string providersPath,
        string? globalStatePath = null)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? id)
        {
            var trimmed = (id ?? string.Empty).Trim();
            if (trimmed.Length == 0 || trimmed.Length > 256 || !seen.Add(trimmed))
            {
                return;
            }

            ids.Add(trimmed);
        }

        try
        {
            if (File.Exists(providersPath)
                && TryReadJsonObject(providersPath, out var providersRoot, out _)
                && providersRoot is not null)
            {
                var settings = ReadOpenAiCompatibleSettings(providersRoot);
                if (settings is not null
                    && PointsAtFluxMuxPort(Str(settings, "baseUrl"), fluxMuxPort))
                {
                    Add(Str(settings, "model"));
                }
            }

            if (File.Exists(modelsPath)
                && TryReadJsonObject(modelsPath, out var modelsRoot, out _)
                && modelsRoot is not null)
            {
                var block = (modelsRoot["providers"] as JsonObject)?[ProviderId] as JsonObject;
                var provider = block?["provider"] as JsonObject;
                if (block is not null
                    && PointsAtFluxMuxPort(Str(provider, "baseUrl"), fluxMuxPort))
                {
                    Add(Str(provider, "defaultModelId"));
                    if (block["models"] is JsonObject modelMap)
                    {
                        foreach (var pair in modelMap)
                        {
                            Add(pair.Key);
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(globalStatePath)
                && File.Exists(globalStatePath)
                && TryReadJsonObject(globalStatePath, out var stateRoot, out _)
                && stateRoot is not null
                && PointsAtFluxMuxPort(Str(stateRoot, "openAiBaseUrl"), fluxMuxPort))
            {
                Add(Str(stateRoot, "planModeOpenAiModelId"));
                Add(Str(stateRoot, "actModeOpenAiModelId"));
                Add(Str(stateRoot, "openAiModelId"));
            }
        }
        catch
        {
        }

        return ids;
    }

    private static bool PatchProvidersSelectedModel(string providersPath, JsonObject? providersRoot, string visibleId)
    {
        if (string.IsNullOrWhiteSpace(providersPath)
            || providersRoot is null
            || string.IsNullOrWhiteSpace(visibleId))
        {
            return false;
        }

        var settings = ReadOpenAiCompatibleSettings(providersRoot);
        if (settings is null)
        {
            return false;
        }

        if (string.Equals(Str(settings, "model"), visibleId, StringComparison.Ordinal))
        {
            return false;
        }

        settings["model"] = visibleId;
        WriteJsonAtomic(providersPath, providersRoot, backupExisting: File.Exists(providersPath));
        return true;
    }

    private static bool PatchGlobalState(
        string? path,
        int contextWindow,
        bool imagesOn,
        string displayName,
        int maxTokens)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        if (!TryReadJsonObject(path, out var root, out _) || root is null)
        {
            return false;
        }

        var changed = ApplyAutoCondenseOff(root)
            | ApplyOpenAiModelInfo(root, contextWindow, imagesOn, displayName, maxTokens);
        if (!changed)
        {
            return false;
        }

        WriteJsonAtomic(path, root, backupExisting: true);
        return true;
    }

    private static bool PatchGlobalSettingsAutoCompactOff(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        if (!TryReadJsonObject(path, out var root, out _) || root is null)
        {
            return false;
        }

        if (root["autoCompactEnabled"] is JsonValue existing
            && existing.TryGetValue<bool>(out var on)
            && !on)
        {
            return false;
        }

        if (root["autoCompactEnabled"] is null)
        {
            return false;
        }

        root["autoCompactEnabled"] = false;
        WriteJsonAtomic(path, root, backupExisting: true);
        return true;
    }

    private static string BuildSyncMessage(int contextWindow, bool contextChanged, bool compactChanged)
    {
        var context = "Cline 4.x Context Window was set to "
            + contextWindow.ToString(CultureInfo.InvariantCulture)
            + " to match this model profile";
        var compact = "Cline Auto compact was turned off so FluxMux Compact can run";
        var follow = "Start a new Cline task. If the bar or Auto compact toggle is unchanged, reload the editor that has Cline.";
        if (contextChanged && compactChanged)
        {
            return context + ". " + compact + ". " + follow;
        }

        if (compactChanged)
        {
            return compact + ". " + follow;
        }

        if (contextChanged)
        {
            return context + ". " + follow;
        }

        return "Cline 4.x Context Window already matches this model profile, and Auto compact is off.";
    }

    private static void WriteJsonAtomic(string path, JsonObject root, bool backupExisting)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (backupExisting && File.Exists(path))
        {
            File.WriteAllText(path + ".bak", ReadTextShared(path), Utf8NoBom);
        }

        var json = root.ToJsonString(JsonWrite) + Environment.NewLine;
        var tempPath = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
        File.WriteAllText(tempPath, json, Utf8NoBom);
        File.Move(tempPath, path, overwrite: true);
    }

    private static bool TryReadPositiveInt(JsonNode? node, out int value)
    {
        value = 0;
        if (node is JsonValue jsonValue && jsonValue.TryGetValue(out int parsed) && parsed > 0)
        {
            value = parsed;
            return true;
        }

        return int.TryParse(node?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            && value > 0;
    }

    private static JsonObject? ReadOpenAiCompatibleSettings(JsonObject? providersRoot)
    {
        var providers = providersRoot?["providers"] as JsonObject;
        var entry = providers?[ProviderId] as JsonObject;
        return entry?["settings"] as JsonObject;
    }

    private static string ReadTextShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static bool TryReadJsonObject(string path, out JsonObject? root, out string error)
    {
        root = null;
        error = string.Empty;
        try
        {
            var text = ReadTextShared(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                root = new JsonObject();
                return true;
            }

            var parsed = JsonNode.Parse(text) as JsonObject;
            if (parsed is null)
            {
                error = "Cline file is not a JSON object: " + path;
                return false;
            }

            root = parsed;
            return true;
        }
        catch (Exception ex)
        {
            error = "Could not read " + path + ": " + ex.Message;
            return false;
        }
    }

    private static ClineContextSyncResult Fail(string modelsPath, string message)
        => new(false, false, false, modelsPath, message);

    private static string Str(JsonObject? obj, string key)
    {
        if (obj is null)
        {
            return string.Empty;
        }

        var node = obj[key];
        if (node is null)
        {
            return string.Empty;
        }

        var text = node.ToString()?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(text) ? string.Empty : text;
    }
}
