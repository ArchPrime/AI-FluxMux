using System.IO;

namespace FluxMux.Avalonia.Services;

internal sealed class HarnessEndpointAdapter(string id) : IEndpointSettingsAdapter
{
    public string Id { get; } = id;

    public EndpointSettingsSyncResult Apply(EndpointSettingsSnapshot snapshot)
    {
        var path = DeepSeekHarnessSetup.ResolveSettingsPath();
        if (!snapshot.CreateHarnessSettingsIfMissing && !File.Exists(path))
        {
            return new EndpointSettingsSyncResult(
                Id,
                true,
                false,
                true,
                "Harness settings.yaml is not on this PC yet.");
        }

        var result = DeepSeekHarnessSetup.MergeIntoSettingsFile(
            DeepSeekHarnessSetupOptions.Create(
                snapshot.Port,
                snapshot.AdvertisedContextWindow,
                snapshot.AdvertisedMaxTokens,
                snapshot.ImagesOn,
                snapshot.ReasoningOn,
                snapshot.DisplayName));
        return new EndpointSettingsSyncResult(
            Id,
            result.Success,
            result.CreatedNewFile || result.BackedUpExistingFile || result.Success,
            false,
            result.Message);
    }
}
