using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Portable Endpoint-adapter pack. No cloud keys, no model profiles.
/// Export before a JSON edit; import after a bad edit or a fresh AI-FluxMux install.
/// </summary>
public static class EndpointAdapterPack
{
    public const string Protocol = "FluxMux";
    public const string Kind = "endpoint-adapters";
    public const string FilePrefix = "fluxmux-endpoint-adapters";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonWrite = new() { WriteIndented = true };

    public static JsonObject Build(FluxMuxConfigDocument config, int port, string endpointApp, int harnessWebPort)
    {
        var adapters = config.Root[EndpointAdapterCatalog.ConfigKey] as JsonArray;
        var pack = new JsonObject
        {
            ["protocol"] = Protocol,
            ["kind"] = Kind,
            ["exportedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["Port"] = Math.Clamp(port, 1, 65535),
            ["EndpointApp"] = string.IsNullOrWhiteSpace(endpointApp) ? "Cline" : endpointApp.Trim(),
            ["HarnessWebPort"] = Math.Clamp(harnessWebPort, 1, 65535)
        };

        if (adapters is not null)
        {
            pack[EndpointAdapterCatalog.ConfigKey] = adapters.DeepClone();
        }

        return pack;
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

            if (LooksLikeSecrets(root))
            {
                error = "That file looks like a secrets or full config file. Use an Endpoint adapters export, not fluxmux_secrets.json.";
                return false;
            }

            var protocol = root["protocol"]?.ToString()?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(protocol)
                && !protocol.Equals(Protocol, StringComparison.OrdinalIgnoreCase))
            {
                error = "That file is not an AI-FluxMux Endpoint adapters pack.";
                return false;
            }

            var kind = root["kind"]?.ToString()?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(kind)
                && !kind.Equals(Kind, StringComparison.OrdinalIgnoreCase))
            {
                error = "That file is not an Endpoint adapters pack.";
                return false;
            }

            if (root[EndpointAdapterCatalog.ConfigKey] is null
                && root["Port"] is null
                && root["OrchestratorPort"] is null
                && root["EndpointApp"] is null)
            {
                error = "That file has no EndpointAdapters, Port, or Client app to import.";
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

    public static string Apply(FluxMuxConfigDocument config, JsonObject pack)
    {
        if (pack[EndpointAdapterCatalog.ConfigKey] is JsonArray adapters)
        {
            config.Root[EndpointAdapterCatalog.ConfigKey] = adapters.DeepClone();
        }

        if (TryReadPositiveInt(pack["Port"], out var port)
            || TryReadPositiveInt(pack["OrchestratorPort"], out port))
        {
            config.SetInt("OrchestratorPort", Math.Clamp(port, 1, 65535));
        }

        var endpointApp = pack["EndpointApp"]?.ToString()?.Trim();
        if (!string.IsNullOrWhiteSpace(endpointApp))
        {
            config.SetString("EndpointApp", endpointApp);
        }

        if (TryReadPositiveInt(pack["HarnessWebPort"], out var harnessPort))
        {
            config.SetInt("HarnessWebPort", Math.Clamp(harnessPort, 1, 65535));
        }

        var ignoredProfiles = pack["LocalProfiles"] is not null || pack["CloudProfiles"] is not null
            ? " Model profiles in that file were ignored."
            : string.Empty;
        return "Endpoint adapters were imported. Close and relaunch AI-FluxMux if Port or a json-merge path changed."
            + ignoredProfiles;
    }

    public static string BackupConfigFile(string configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            return string.Empty;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var backup = configPath + ".bak-endpoint-" + stamp;
        try
        {
            File.Copy(configPath, backup, overwrite: false);
        }
        catch (IOException)
        {
            backup = configPath + ".bak-endpoint-" + stamp + "-" + Guid.NewGuid().ToString("N")[..8];
            File.Copy(configPath, backup, overwrite: false);
        }

        return backup;
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

    private static bool LooksLikeSecrets(JsonObject root)
        => root["CloudKeys"] is not null
           || root["ApiKey"] is not null
           || root["fluxmux_secrets"] is not null;

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
}
