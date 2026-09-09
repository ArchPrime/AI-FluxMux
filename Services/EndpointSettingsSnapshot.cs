using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Shared values every Client-app adapter can write. Cline uses the loaded
/// Context; Harness may advertise a larger validated local so a picture or
/// bigger turn can reach Port.
/// </summary>
public readonly record struct EndpointSettingsSnapshot(
    int Port,
    int LoadedContextWindow,
    int AdvertisedContextWindow,
    int AdvertisedMaxTokens,
    bool ImagesOn,
    bool ReasoningOn,
    string DisplayName,
    string ModelId,
    bool CreateHarnessSettingsIfMissing)
{
    public string Fingerprint()
        => string.Join(
            "|",
            Port.ToString(CultureInfo.InvariantCulture),
            LoadedContextWindow.ToString(CultureInfo.InvariantCulture),
            AdvertisedContextWindow.ToString(CultureInfo.InvariantCulture),
            AdvertisedMaxTokens.ToString(CultureInfo.InvariantCulture),
            ImagesOn ? "img" : "noimg",
            ReasoningOn ? "reason" : "noreason",
            string.IsNullOrWhiteSpace(DisplayName) ? FluxMuxGatewayModels.LocalModelId : DisplayName.Trim(),
            string.IsNullOrWhiteSpace(ModelId) ? FluxMuxGatewayModels.LocalModelId : ModelId.Trim(),
            CreateHarnessSettingsIfMissing ? "mkharness" : "noharness");
}

public readonly record struct EndpointSettingsSyncResult(
    string Id,
    bool Success,
    bool Changed,
    bool Skipped,
    string Message);

public readonly record struct EndpointSettingsSyncBatch(IReadOnlyList<EndpointSettingsSyncResult> Results)
{
    public EndpointSettingsSyncResult? Find(string id)
        => Results.FirstOrDefault(result => result.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

public readonly record struct EndpointAdapterDefinition(
    string Id,
    string Kind,
    bool Enabled,
    string Path,
    bool OnlyIfFileExists,
    string BaseUrlPath,
    IReadOnlyDictionary<string, string> Set);
