using System;
using System.Collections.Generic;

namespace FluxMux.Avalonia.Services;

public static class EndpointSettingsSync
{
    public static EndpointSettingsSyncBatch Apply(
        EndpointSettingsSnapshot snapshot,
        FluxMuxConfigDocument? config)
        => Apply(snapshot, EndpointAdapterCatalog.CreateAdapters(EndpointAdapterCatalog.Resolve(config)));

    public static EndpointSettingsSyncBatch Apply(
        EndpointSettingsSnapshot snapshot,
        IEnumerable<IEndpointSettingsAdapter> adapters)
    {
        var results = new List<EndpointSettingsSyncResult>();
        foreach (var adapter in adapters)
        {
            try
            {
                results.Add(adapter.Apply(snapshot));
            }
            catch (Exception ex)
            {
                results.Add(new EndpointSettingsSyncResult(
                    adapter.Id,
                    false,
                    false,
                    false,
                    "Could not update " + adapter.Id + ": " + ex.Message));
            }
        }

        return new EndpointSettingsSyncBatch(results);
    }
}
