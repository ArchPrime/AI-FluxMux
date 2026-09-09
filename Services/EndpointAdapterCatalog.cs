using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Built-in Client-app writers plus optional fluxmux_config.json
/// <c>EndpointAdapters</c> entries. Missing key = Cline 4.x and Harness yaml
/// as today. A listed id can set enabled false. kind json-merge adds a client.
/// </summary>
public static class EndpointAdapterCatalog
{
    public const string ConfigKey = "EndpointAdapters";
    public const string ClineKind = "cline-4x";
    public const string HarnessKind = "harness-yaml";
    public const string JsonMergeKind = "json-merge";
    public const string ClineId = "cline-4x";
    public const string HarnessId = "harness-yaml";

    public static IReadOnlyList<EndpointAdapterDefinition> BuiltIns { get; } =
    [
        new EndpointAdapterDefinition(ClineId, ClineKind, true, string.Empty, true, string.Empty, new Dictionary<string, string>()),
        new EndpointAdapterDefinition(HarnessId, HarnessKind, true, string.Empty, true, string.Empty, new Dictionary<string, string>())
    ];

    public static IReadOnlyList<EndpointAdapterDefinition> Resolve(FluxMuxConfigDocument? config)
        => Resolve(config?.GetObjectArray(ConfigKey) ?? Array.Empty<JsonObject>());

    public static IReadOnlyList<EndpointAdapterDefinition> Resolve(IReadOnlyList<JsonObject> listed)
    {
        var byId = new Dictionary<string, EndpointAdapterDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var builtIn in BuiltIns)
        {
            byId[builtIn.Id] = builtIn;
        }

        foreach (var node in listed)
        {
            var parsed = Parse(node);
            if (string.IsNullOrWhiteSpace(parsed.Id) || string.IsNullOrWhiteSpace(parsed.Kind))
            {
                continue;
            }

            byId[parsed.Id] = parsed;
        }

        return byId.Values.ToList();
    }

    public static IEnumerable<IEndpointSettingsAdapter> CreateAdapters(IReadOnlyList<EndpointAdapterDefinition> definitions)
    {
        foreach (var definition in definitions)
        {
            if (!definition.Enabled)
            {
                continue;
            }

            var adapter = CreateAdapter(definition);
            if (adapter is not null)
            {
                yield return adapter;
            }
        }
    }

    public static IEndpointSettingsAdapter? CreateAdapter(EndpointAdapterDefinition definition)
    {
        if (definition.Kind.Equals(ClineKind, StringComparison.OrdinalIgnoreCase))
        {
            return new ClineEndpointAdapter(definition.Id);
        }

        if (definition.Kind.Equals(HarnessKind, StringComparison.OrdinalIgnoreCase))
        {
            return new HarnessEndpointAdapter(definition.Id);
        }

        if (definition.Kind.Equals(JsonMergeKind, StringComparison.OrdinalIgnoreCase))
        {
            return new JsonMergeEndpointAdapter(definition);
        }

        return null;
    }

    internal static EndpointAdapterDefinition Parse(JsonObject node)
    {
        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node["set"] is JsonObject setObject)
        {
            foreach (var pair in setObject)
            {
                var value = pair.Value?.ToString();
                if (!string.IsNullOrWhiteSpace(pair.Key) && value is not null)
                {
                    set[pair.Key] = value;
                }
            }
        }

        var enabled = true;
        if (node["enabled"] is JsonValue enabledNode && enabledNode.TryGetValue<bool>(out var on))
        {
            enabled = on;
        }

        var onlyIfFileExists = true;
        if (node["onlyIfFileExists"] is JsonValue onlyIfNode && onlyIfNode.TryGetValue<bool>(out var onlyIf))
        {
            onlyIfFileExists = onlyIf;
        }

        return new EndpointAdapterDefinition(
            (node["id"]?.ToString() ?? string.Empty).Trim(),
            (node["kind"]?.ToString() ?? string.Empty).Trim(),
            enabled,
            (node["path"]?.ToString() ?? string.Empty).Trim(),
            onlyIfFileExists,
            (node["baseUrlPath"]?.ToString() ?? string.Empty).Trim(),
            set);
    }
}

public interface IEndpointSettingsAdapter
{
    string Id { get; }

    EndpointSettingsSyncResult Apply(EndpointSettingsSnapshot snapshot);
}
