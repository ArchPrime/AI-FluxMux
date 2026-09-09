using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Temporary model-profile transfer file. Never the live store.
/// Import patches fluxmux_config.json only when the pack is clean, and never
/// overwrites a live row that already exists with different settings.
/// </summary>
public static class ModelProfilePack
{
    public const string Protocol = "FluxMux";
    public const string Kind = "model-profiles";
    public const string FilePrefix = "fluxmux-model-profiles";
    public const string LocalKey = "LocalProfiles";
    public const string CloudKey = "CloudProfiles";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonWrite = new() { WriteIndented = true };
    private static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;

    private static readonly string[] SecretKeyNames =
    [
        "CloudKeys",
        "ApiKey",
        "fluxmux_secrets",
        "Authorization",
        "access_token",
        "client_secret",
        "password"
    ];

    private static readonly string[] ThisPcOnlyKeys =
    [
        "EndpointValidatedUtc",
        "EndpointWarning"
    ];

    public static JsonObject Build(FluxMuxConfigDocument config)
    {
        return new JsonObject
        {
            ["protocol"] = Protocol,
            ["kind"] = Kind,
            ["exportedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            [LocalKey] = CopyProfiles(config.Root[LocalKey] as JsonObject, local: true),
            [CloudKey] = CopyProfiles(config.Root[CloudKey] as JsonObject, local: false)
        };
    }

    public static bool TryRead(string json, out JsonObject pack, out string error)
    {
        pack = new JsonObject();
        error = string.Empty;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
            {
                error = "That file is not a JSON object.";
                return false;
            }

            if (ContainsSecrets(root))
            {
                error = "That file contains secrets or API keys. Model profile packs must not include fluxmux_secrets.json or cloud keys.";
                return false;
            }

            var protocol = root["protocol"]?.ToString()?.Trim() ?? string.Empty;
            if (!protocol.Equals(Protocol, StringComparison.OrdinalIgnoreCase))
            {
                error = "That file is not an AI-FluxMux model profile pack.";
                return false;
            }

            var kind = root["kind"]?.ToString()?.Trim() ?? string.Empty;
            if (kind.Equals(EndpointAdapterPack.Kind, StringComparison.OrdinalIgnoreCase))
            {
                error = "That file is an Endpoint adapters pack. Use Import Endpoint adapters on Servers.";
                return false;
            }

            if (!kind.Equals(Kind, StringComparison.OrdinalIgnoreCase))
            {
                error = "That file is not a model profile pack. Use Export model profiles on Model Profiles.";
                return false;
            }

            if (LooksLikeLiveConfig(root))
            {
                error = "That file looks like a live fluxmux_config.json. Export writes a temporary pack; do not import the whole config.";
                return false;
            }

            if (!TryValidateProfileMap(root[LocalKey], local: true, out error)
                || !TryValidateProfileMap(root[CloudKey], local: false, out error))
            {
                return false;
            }

            var localCount = CountProfiles(root[LocalKey] as JsonObject);
            var cloudCount = CountProfiles(root[CloudKey] as JsonObject);
            if (localCount + cloudCount == 0)
            {
                error = "That pack has no LocalProfiles or CloudProfiles to import.";
                return false;
            }

            pack = root;
            return true;
        }
        catch (Exception ex)
        {
            error = "Could not read that JSON: " + ex.Message;
            return false;
        }
    }

    public static ModelProfilePackApplyResult Apply(
        FluxMuxConfigDocument config,
        JsonObject pack,
        string? localModelDirectory = null)
    {
        var added = 0;
        var alreadyPresent = 0;
        var sameNameDifferentSettings = 0;
        var sameSettingsDifferentName = 0;
        var missingGguf = 0;
        var missingProjector = 0;
        var skips = new List<ModelProfilePackSkip>();

        added += PatchMap(
            config,
            LocalKey,
            pack[LocalKey] as JsonObject,
            local: true,
            localModelDirectory,
            skips,
            ref alreadyPresent,
            ref sameNameDifferentSettings,
            ref sameSettingsDifferentName,
            ref missingGguf,
            ref missingProjector);
        added += PatchMap(
            config,
            CloudKey,
            pack[CloudKey] as JsonObject,
            local: false,
            localModelDirectory: null,
            skips,
            ref alreadyPresent,
            ref sameNameDifferentSettings,
            ref sameSettingsDifferentName,
            ref missingGguf,
            ref missingProjector);

        return new ModelProfilePackApplyResult(
            added,
            alreadyPresent,
            sameNameDifferentSettings,
            sameSettingsDifferentName,
            missingGguf,
            missingProjector,
            skips);
    }

    public static string BackupConfigFile(string configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            return string.Empty;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var backup = configPath + ".bak-profiles-" + stamp;
        try
        {
            File.Copy(configPath, backup, overwrite: false);
        }
        catch (IOException)
        {
            backup = configPath + ".bak-profiles-" + stamp + "-" + Guid.NewGuid().ToString("N")[..8];
            File.Copy(configPath, backup, overwrite: false);
        }

        return backup;
    }

    public static string WritePreImportBackup(FluxMuxConfigDocument config, string configPath)
    {
        var directory = Path.GetDirectoryName(configPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Directory.GetCurrentDirectory();
        }

        Directory.CreateDirectory(directory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, FilePrefix + "-pre-import-" + stamp + ".json");
        if (File.Exists(path))
        {
            path = Path.Combine(
                directory,
                FilePrefix + "-pre-import-" + stamp + "-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        }

        WritePack(path, Build(config));
        return path;
    }

    public static void WritePack(string path, JsonObject pack)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, pack.ToJsonString(JsonWrite) + Environment.NewLine, Utf8NoBom);
    }

    private static int PatchMap(
        FluxMuxConfigDocument config,
        string mapKey,
        JsonObject? incoming,
        bool local,
        string? localModelDirectory,
        List<ModelProfilePackSkip> skips,
        ref int alreadyPresent,
        ref int sameNameDifferentSettings,
        ref int sameSettingsDifferentName,
        ref int missingGguf,
        ref int missingProjector)
    {
        if (incoming is null || incoming.Count == 0)
        {
            return 0;
        }

        if (config.Root[mapKey] is not JsonObject live)
        {
            live = new JsonObject();
            config.Root[mapKey] = live;
        }

        var added = 0;
        var checkLocalFiles = local && localModelDirectory is not null;
        foreach (var entry in incoming)
        {
            if (entry.Value is not JsonObject incomingProfile)
            {
                continue;
            }

            var key = entry.Key;
            var incomingClean = SanitizeProfile(incomingProfile);
            var incomingFingerprint = Fingerprint(incomingClean, local);

            var matchingLiveKey = live
                .Select(pair => pair.Key)
                .FirstOrDefault(existing => KeyComparer.Equals(existing, key));

            if (!string.IsNullOrWhiteSpace(matchingLiveKey)
                && live[matchingLiveKey] is JsonObject existing)
            {
                if (Fingerprint(existing, local).Equals(incomingFingerprint, StringComparison.Ordinal))
                {
                    alreadyPresent++;
                    continue;
                }

                sameNameDifferentSettings++;
                skips.Add(Skip(key, ModelProfilePackSkip.SameNameDifferentSettings,
                    "same name, different settings (live row kept)."));
                continue;
            }

            var duplicateName = FindSameSettingsDifferentName(live, key, incomingFingerprint, local);
            if (!string.IsNullOrWhiteSpace(duplicateName))
            {
                sameSettingsDifferentName++;
                skips.Add(Skip(key, ModelProfilePackSkip.SameSettingsDifferentName,
                    "same settings already on another variant of that model (" + duplicateName + ")."));
                continue;
            }

            if (checkLocalFiles)
            {
                var ggufName = ProfileFamily(key, local: true);
                if (!TryFindLocalGguf(localModelDirectory, ggufName, out var ggufPath))
                {
                    missingGguf++;
                    skips.Add(Skip(key, ModelProfilePackSkip.MissingGguf,
                        "that local model is not in the Model Directory."));
                    continue;
                }

                if (ProfileSettingsFingerprint.NormalizeVisionEnabled(incomingClean["LocalVisionEnabled"]?.ToString())
                    .Equals("enabled", StringComparison.Ordinal))
                {
                    if (!TryFindVisionProjector(localModelDirectory, ggufPath, incomingClean, out var projectorPath))
                    {
                        missingProjector++;
                        skips.Add(Skip(key, ModelProfilePackSkip.MissingProjector,
                            "Images is on but no vision projector (mmproj) file was found."));
                        continue;
                    }

                    incomingClean["LocalVisionProjectorPath"] = projectorPath;
                }
            }

            live[key] = incomingClean;
            added++;
        }

        return added;
    }

    private static ModelProfilePackSkip Skip(string key, string reason, string detail)
        => new(key, reason, "Import skipped " + key + ": " + detail);

    public static bool TryFindLocalGguf(string? modelDirectory, string fileName, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(modelDirectory)
            || !Directory.Exists(modelDirectory)
            || string.IsNullOrWhiteSpace(fileName)
            || LocalHealthGuidance.IsVisionProjectorFile(fileName))
        {
            return false;
        }

        try
        {
            var match = Directory.EnumerateFiles(modelDirectory, "*.gguf", SearchOption.AllDirectories)
                .FirstOrDefault(candidate =>
                    Path.GetFileName(candidate).Equals(fileName, StringComparison.OrdinalIgnoreCase)
                    && !LocalHealthGuidance.IsVisionProjectorFile(candidate));
            if (string.IsNullOrWhiteSpace(match))
            {
                return false;
            }

            path = match;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryFindVisionProjector(
        string? modelDirectory,
        string ggufPath,
        JsonObject profile,
        out string path)
    {
        path = string.Empty;
        var packed = profile["LocalVisionProjectorPath"]?.ToString()?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(packed)
            && File.Exists(packed)
            && LocalHealthGuidance.IsVisionProjectorFile(packed))
        {
            path = Path.GetFullPath(packed);
            return true;
        }

        if (string.IsNullOrWhiteSpace(modelDirectory) || !Directory.Exists(modelDirectory))
        {
            return false;
        }

        try
        {
            var packedName = Path.GetFileName(packed);
            foreach (var candidate in Directory.EnumerateFiles(modelDirectory, "*.gguf", SearchOption.AllDirectories))
            {
                if (!LocalHealthGuidance.IsVisionProjectorFile(candidate))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(packedName)
                    && Path.GetFileName(candidate).Equals(packedName, StringComparison.OrdinalIgnoreCase))
                {
                    path = candidate;
                    return true;
                }

                if (FluxMuxRuntimeService.VisionProjectorMatchesModel(ggufPath, candidate))
                {
                    path = candidate;
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static string? FindSameSettingsDifferentName(
        JsonObject live,
        string incomingKey,
        string incomingFingerprint,
        bool local)
    {
        var incomingFamily = ProfileFamily(incomingKey, local);
        foreach (var entry in live)
        {
            if (KeyComparer.Equals(entry.Key, incomingKey) || entry.Value is not JsonObject existing)
            {
                continue;
            }

            if (!KeyComparer.Equals(ProfileFamily(entry.Key, local), incomingFamily))
            {
                continue;
            }

            if (Fingerprint(existing, local).Equals(incomingFingerprint, StringComparison.Ordinal))
            {
                return entry.Key;
            }
        }

        return null;
    }

    private static string Fingerprint(JsonObject settings, bool local)
        => local ? ProfileSettingsFingerprint.Local(settings) : ProfileSettingsFingerprint.Cloud(settings);

    private static string ProfileFamily(string key, bool local)
    {
        if (local)
        {
            var parts = key.Split("::", 2, StringSplitOptions.TrimEntries);
            return parts.Length == 0 ? string.Empty : parts[0];
        }

        var cloud = key.Split("::", StringSplitOptions.TrimEntries);
        return cloud.Length < 2 ? string.Empty : cloud[0] + "::" + cloud[1];
    }

    private static JsonObject CopyProfiles(JsonObject? source, bool local)
    {
        var copy = new JsonObject();
        if (source is null)
        {
            return copy;
        }

        foreach (var entry in source)
        {
            if (entry.Value is not JsonObject profile)
            {
                continue;
            }

            if (local ? !IsLocalProfileKey(entry.Key) : !IsCloudProfileKey(entry.Key))
            {
                continue;
            }

            copy[entry.Key] = SanitizeProfile(profile);
        }

        return copy;
    }

    private static JsonObject SanitizeProfile(JsonObject profile)
    {
        var copy = profile.DeepClone() as JsonObject ?? new JsonObject();
        foreach (var key in ThisPcOnlyKeys)
        {
            copy.Remove(key);
        }

        var secretKeys = copy
            .Select(pair => pair.Key)
            .Where(IsSecretKey)
            .ToList();
        foreach (var key in secretKeys)
        {
            copy.Remove(key);
        }

        return copy;
    }

    private static bool TryValidateProfileMap(JsonNode? node, bool local, out string error)
    {
        error = string.Empty;
        if (node is null)
        {
            return true;
        }

        if (node is not JsonObject map)
        {
            error = local
                ? "LocalProfiles must be a JSON object of model profile rows."
                : "CloudProfiles must be a JSON object of model profile rows.";
            return false;
        }

        foreach (var entry in map)
        {
            if (local ? !IsLocalProfileKey(entry.Key) : !IsCloudProfileKey(entry.Key))
            {
                error = "Profile key '" + entry.Key + "' is not a valid "
                    + (local ? "local ModelFileName.gguf::Variant" : "cloud Provider::ModelId::Variant")
                    + " name.";
                return false;
            }

            if (entry.Value is not JsonObject profile)
            {
                error = "Profile '" + entry.Key + "' must be a JSON object of settings.";
                return false;
            }

            if (!TryValidateProfileObject(entry.Key, profile, out error))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryValidateProfileObject(string key, JsonObject profile, out string error)
    {
        error = string.Empty;
        foreach (var entry in profile)
        {
            if (IsSecretKey(entry.Key))
            {
                error = "Profile '" + key + "' contains a secret key and was rejected.";
                return false;
            }

            if (entry.Value is JsonObject)
            {
                error = "Profile '" + key + "' contains nested objects. A model profile pack may only hold settings values.";
                return false;
            }

            if (entry.Value is JsonArray array && array.Any(item => item is JsonObject))
            {
                error = "Profile '" + key + "' contains nested objects in an array.";
                return false;
            }

            if (IsBreakingText(entry.Value?.ToString()))
            {
                error = "Profile '" + key + "' contains script or markup that FluxMux will not import.";
                return false;
            }
        }

        return true;
    }

    public static bool IsLocalProfileKey(string key)
    {
        var parts = (key ?? string.Empty).Split("::", 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || string.IsNullOrWhiteSpace(parts[1])
            || !parts[0].EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
            || parts[0].Contains("mmproj", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public static bool IsCloudProfileKey(string key)
    {
        var parts = (key ?? string.Empty).Split("::", StringSplitOptions.TrimEntries);
        return parts.Length == 3
            && !string.IsNullOrWhiteSpace(parts[0])
            && !string.IsNullOrWhiteSpace(parts[1])
            && !string.IsNullOrWhiteSpace(parts[2])
            && !parts[0].Contains(".gguf", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeLiveConfig(JsonObject root)
        => root["OrchestratorPort"] is not null
           || root["EndpointAdapters"] is not null
           || root["ModelDirectory"] is not null
           || root["LocalServerExecutablePath"] is not null
           || root["RouteSlots"] is not null;

    private static bool ContainsSecrets(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj)
                {
                    if (IsSecretKey(pair.Key) || ContainsSecrets(pair.Value))
                    {
                        return true;
                    }
                }

                return false;
            case JsonArray array:
                return array.Any(ContainsSecrets);
            default:
                return false;
        }
    }

    private static bool IsSecretKey(string? key)
    {
        var text = (key ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (SecretKeyNames.Any(name => text.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return text.EndsWith("ApiKey", StringComparison.OrdinalIgnoreCase)
            || text.EndsWith("Secret", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBreakingText(string? value)
    {
        var text = value ?? string.Empty;
        return text.Contains("<script", StringComparison.OrdinalIgnoreCase)
            || text.Contains("javascript:", StringComparison.OrdinalIgnoreCase);
    }

    private static int CountProfiles(JsonObject? map)
        => map?.Count(pair => pair.Value is JsonObject) ?? 0;
}

public readonly record struct ModelProfilePackSkip(string Key, string Reason, string Message)
{
    public const string MissingGguf = "missing-gguf";
    public const string MissingProjector = "missing-projector";
    public const string SameNameDifferentSettings = "same-name-different-settings";
    public const string SameSettingsDifferentName = "same-settings-different-name";
}

public readonly record struct ModelProfilePackApplyResult(
    int Added,
    int AlreadyPresent,
    int SameNameDifferentSettings,
    int SameSettingsDifferentName,
    int MissingGguf = 0,
    int MissingProjector = 0,
    IReadOnlyList<ModelProfilePackSkip>? Skips = null)
{
    public bool Changed => Added > 0;

    public IReadOnlyList<ModelProfilePackSkip> SkipDetails => Skips ?? Array.Empty<ModelProfilePackSkip>();

    public string Message
    {
        get
        {
            var parts = new List<string>();
            if (Added > 0)
            {
                parts.Add("Patched " + Added.ToString(CultureInfo.InvariantCulture) + " model profile(s) into the live config.");
            }
            else
            {
                parts.Add("No model profiles were added.");
            }

            if (AlreadyPresent > 0)
            {
                parts.Add(AlreadyPresent.ToString(CultureInfo.InvariantCulture) + " already matched a live row.");
            }

            if (SameNameDifferentSettings > 0)
            {
                parts.Add(SameNameDifferentSettings.ToString(CultureInfo.InvariantCulture)
                    + " skipped — same name, different settings (live row kept).");
            }

            if (SameSettingsDifferentName > 0)
            {
                parts.Add(SameSettingsDifferentName.ToString(CultureInfo.InvariantCulture)
                    + " skipped — same settings already on another variant of that model.");
            }

            if (MissingGguf > 0)
            {
                parts.Add(MissingGguf.ToString(CultureInfo.InvariantCulture)
                    + " skipped — local model (.gguf) is not in the Model Directory.");
            }

            if (MissingProjector > 0)
            {
                parts.Add(MissingProjector.ToString(CultureInfo.InvariantCulture)
                    + " skipped — Images is on but no vision projector (mmproj) was found.");
            }

            if (SkipDetails.Count > 0)
            {
                parts.Add("Open Diagnostics for each skipped row.");
            }

            if (Added > 0)
            {
                parts.Add("Run Validate to endpoint on new rows before Quick Select.");
            }

            return string.Join(" ", parts);
        }
    }
}
