namespace FluxMux.Avalonia.ViewModels;

/// <summary>
/// Provider / model / variant used by cloud Validate to endpoint.
/// The tree item wins when it has values; empty tree fields must not wipe
/// the current selection, or Launch Cloud fails with "must both be selected."
/// </summary>
public static class CloudProfileLaunchTarget
{
    public static bool TryResolve(
        CloudVariantTreeItemViewModel? item,
        string? selectedProvider,
        string? selectedModel,
        string? selectedVariant,
        string defaultVariant,
        out string provider,
        out string model,
        out string variant)
    {
        provider = FirstFilled(item?.Provider, selectedProvider);
        model = FirstFilled(item?.ModelName, selectedModel);
        variant = FirstFilled(item?.Name, selectedVariant);
        if (string.IsNullOrWhiteSpace(variant))
        {
            variant = defaultVariant ?? string.Empty;
        }

        return !string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(model);
    }

    private static string FirstFilled(string? preferred, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred.Trim();
        }

        return string.IsNullOrWhiteSpace(fallback) ? string.Empty : fallback.Trim();
    }
}
