using System;
using System.Collections.Generic;
using System.Linq;

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

    /// <summary>
    /// Launch still writes every adapter that points at Port. Diagnostics
    /// only names the Client app that is actually in play, so a leftover
    /// Cline settings patch does not show up on a Harness chat.
    /// </summary>
    public static EndpointSettingsSyncResult? PickOperatorNotice(
        IEnumerable<EndpointSettingsSyncResult> results,
        bool preferHarness)
    {
        var changed = results
            .Where(result => result.Changed && !string.IsNullOrWhiteSpace(result.Message))
            .ToList();
        if (changed.Count == 0)
        {
            return null;
        }

        if (preferHarness)
        {
            return FirstChanged(changed, EndpointAdapterCatalog.HarnessId)
                ?? FirstChangedExcept(changed, EndpointAdapterCatalog.ClineId);
        }

        return FirstChanged(changed, EndpointAdapterCatalog.ClineId)
            ?? changed[0];
    }

    private static EndpointSettingsSyncResult? FirstChanged(
        IReadOnlyList<EndpointSettingsSyncResult> changed,
        string id)
    {
        foreach (var result in changed)
        {
            if (result.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }
        }

        return null;
    }

    private static EndpointSettingsSyncResult? FirstChangedExcept(
        IReadOnlyList<EndpointSettingsSyncResult> changed,
        string id)
    {
        foreach (var result in changed)
        {
            if (!result.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }
        }

        return null;
    }
}
