namespace FluxMux.Avalonia.Services;

internal sealed class ClineEndpointAdapter(string id) : IEndpointSettingsAdapter
{
    public string Id { get; } = id;

    public EndpointSettingsSyncResult Apply(EndpointSettingsSnapshot snapshot)
    {
        if (snapshot.LoadedContextWindow <= 0)
        {
            return new EndpointSettingsSyncResult(Id, true, false, true, "Cline 4.x sync skipped: no loaded Context.");
        }

        var result = ClineContextSync.MergeIntoModelsFile(
            ClineContextSyncOptions.Create(
                snapshot.Port,
                snapshot.LoadedContextWindow,
                snapshot.ModelId,
                snapshot.ImagesOn,
                snapshot.DisplayName,
                snapshot.AdvertisedMaxTokens));
        return new EndpointSettingsSyncResult(Id, result.Success, result.Changed, result.Skipped, result.Message);
    }
}
