using System.Collections.Generic;
using System.Linq;

namespace FluxMux.Avalonia.ViewModels;

public static class VariantTreeSelection
{
    public static CloudVariantTreeItemViewModel? ResolveActive(
        IEnumerable<CloudVariantTreeItemViewModel> items,
        CloudVariantTreeItemViewModel? selected)
    {
        if (selected is { IsExpanded: true })
        {
            return selected;
        }

        return items.FirstOrDefault(item => item.IsExpanded) ?? selected;
    }

    /// <summary>
    /// The row that is actually open. Does not fall back to a collapsed selection,
    /// so a queued reassert cannot expand an unrelated cloud or local profile.
    /// </summary>
    public static CloudVariantTreeItemViewModel? ResolveExpanded(
        IEnumerable<CloudVariantTreeItemViewModel> items,
        CloudVariantTreeItemViewModel? selected)
    {
        if (selected is { IsExpanded: true })
        {
            return selected;
        }

        return items.FirstOrDefault(item => item.IsExpanded);
    }

    public static void ExpandOnly(
        IEnumerable<CloudVariantTreeItemViewModel> items,
        CloudVariantTreeItemViewModel live)
    {
        foreach (var item in items)
        {
            var shouldExpand = ReferenceEquals(item, live);
            if (item.IsExpanded != shouldExpand)
            {
                item.IsExpanded = shouldExpand;
            }
        }
    }

    public static void CollapseAll(IEnumerable<CloudVariantTreeItemViewModel> items)
    {
        foreach (var item in items)
        {
            if (item.IsExpanded)
            {
                item.IsExpanded = false;
            }
        }
    }

    public static bool ShouldReassert(
        CloudVariantTreeItemViewModel? selected,
        CloudVariantTreeItemViewModel live)
        => !ReferenceEquals(selected, live) || !live.IsExpanded;

    /// <summary>
    /// First click on a closed row selects that row while the previous row
    /// is still expanded. Snap back only when selection was cleared.
    /// </summary>
    public static bool ShouldAdoptStolenSelection(
        CloudVariantTreeItemViewModel? selected,
        CloudVariantTreeItemViewModel? expanded)
    {
        if (expanded is null)
        {
            return false;
        }

        return selected is null;
    }
}
