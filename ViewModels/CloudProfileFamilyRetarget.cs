using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.ViewModels;

/// <summary>
/// Moves a cloud profile family from one model id to another without deleting
/// settings. Used when Validate fails and the operator wants to try a different
/// company model id (Copilot GitHub catalog ids especially).
/// </summary>
public static class CloudProfileFamilyRetarget
{
    public static bool TryRewriteProfiles(
        JsonObject profiles,
        string provider,
        string fromModel,
        string toModel,
        out int movedCount,
        out string error)
        => TryRewriteProfiles(profiles, provider, fromModel, provider, toModel, out movedCount, out error);

    public static bool TryRewriteProfiles(
        JsonObject profiles,
        string fromProvider,
        string fromModel,
        string toProvider,
        string toModel,
        out int movedCount,
        out string error)
    {
        movedCount = 0;
        error = string.Empty;
        fromProvider = (fromProvider ?? string.Empty).Trim();
        fromModel = (fromModel ?? string.Empty).Trim();
        toProvider = (toProvider ?? string.Empty).Trim();
        toModel = (toModel ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(toProvider) || string.IsNullOrWhiteSpace(toModel))
        {
            error = "Provider and the new model id are required.";
            return false;
        }

        if (fromProvider.Equals(toProvider, StringComparison.OrdinalIgnoreCase)
            && fromModel.Equals(toModel, StringComparison.OrdinalIgnoreCase))
        {
            error = "This profile already uses that provider and model id.";
            return false;
        }

        if (FindFamilyKeys(profiles, toProvider, toModel).Count > 0)
        {
            error = "A cloud profile for " + toProvider + " / " + toModel + " already exists. Expand that profile instead.";
            return false;
        }

        var sourceKeys = FindFamilyKeys(profiles, fromProvider, fromModel);
        if (sourceKeys.Count == 0)
        {
            error = "No cloud profile was found for "
                + (string.IsNullOrWhiteSpace(fromProvider) ? "(missing provider)" : fromProvider)
                + " / "
                + (string.IsNullOrWhiteSpace(fromModel) ? "(missing model id)" : fromModel)
                + ".";
            return false;
        }

        foreach (var key in sourceKeys)
        {
            if (!TryParseKey(key, out _, out _, out var variant))
            {
                continue;
            }

            var payload = (profiles[key] as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject();
            payload.Remove("EndpointValidatedUtc");
            payload.Remove("EndpointWarning");
            profiles.Remove(key);
            profiles[$"{toProvider}::{toModel}::{variant}"] = payload;
            movedCount++;
        }

        return movedCount > 0;
    }

    public static int RewriteRouteSlots(JsonArray? slots, string fromProvider, string fromModel, string toModel, string? toProvider = null)
    {
        fromProvider = (fromProvider ?? string.Empty).Trim();
        fromModel = (fromModel ?? string.Empty).Trim();
        toProvider = (toProvider ?? fromProvider).Trim();
        toModel = (toModel ?? string.Empty).Trim();
        if (slots is null || string.IsNullOrWhiteSpace(fromProvider) || string.IsNullOrWhiteSpace(fromModel))
        {
            return 0;
        }

        var changed = 0;
        foreach (var node in slots.OfType<JsonObject>())
        {
            var routeType = node["routeType"]?.ToString() ?? string.Empty;
            if (!routeType.Equals("cloud", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var slotProvider = (node["provider"]?.ToString() ?? string.Empty).Trim();
            var slotModel = (node["cloudModel"]?.ToString() ?? string.Empty).Trim();
            if (!slotProvider.Equals(fromProvider, StringComparison.OrdinalIgnoreCase)
                || !slotModel.Equals(fromModel, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            node["provider"] = toProvider;
            node["cloudModel"] = toModel;
            node["validatedDefaultProfile"] = false;
            node.Remove("endpointValidatedUtc");
            changed++;
        }

        return changed;
    }

    public static bool FamilyExists(JsonObject? profiles, string provider, string model)
        => profiles is not null && FindFamilyKeys(profiles, provider, model).Count > 0;

    public static bool FamilyIsValidated(JsonObject? profiles, string provider, string model)
    {
        if (profiles is null)
        {
            return false;
        }

        foreach (var key in FindFamilyKeys(profiles, provider, model))
        {
            if (profiles[key] is JsonObject settings
                && !string.IsNullOrWhiteSpace(settings["EndpointValidatedUtc"]?.ToString()))
            {
                return true;
            }
        }

        return false;
    }

    public static List<string> FindFamilyKeys(JsonObject profiles, string provider, string model)
    {
        provider = (provider ?? string.Empty).Trim();
        model = (model ?? string.Empty).Trim();
        return profiles
            .Select(entry => entry.Key)
            .Where(key =>
            {
                if (!TryParseKey(key, out var keyProvider, out var keyModel, out _))
                {
                    return false;
                }

                return keyProvider.Equals(provider, StringComparison.OrdinalIgnoreCase)
                    && keyModel.Equals(model, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();
    }

    public static bool TryParseKey(string? key, out string provider, out string model, out string variant)
    {
        provider = string.Empty;
        model = string.Empty;
        variant = string.Empty;
        var raw = key ?? string.Empty;
        var first = raw.IndexOf("::", StringComparison.Ordinal);
        if (first < 0)
        {
            return false;
        }

        provider = raw[..first].Trim();
        var rest = raw[(first + 2)..];
        var second = rest.IndexOf("::", StringComparison.Ordinal);
        if (second < 0)
        {
            if (!rest.StartsWith(':') || rest.Length < 2)
            {
                return false;
            }

            model = string.Empty;
            variant = rest[1..].Trim();
            return !string.IsNullOrWhiteSpace(variant);
        }

        model = rest[..second].Trim();
        variant = rest[(second + 2)..].Trim();
        return !string.IsNullOrWhiteSpace(variant);
    }
}
