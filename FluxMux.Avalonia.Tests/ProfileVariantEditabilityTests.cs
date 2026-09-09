using FluxMux.Avalonia.ViewModels;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class ProfileVariantEditabilityTests
{
    [Fact]
    public void Created_row_wins_when_the_source_expander_is_still_open()
    {
        var source = new CloudVariantTreeItemViewModel(
            "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf",
            "(defaults)",
            isBase: true,
            "summary");
        var created = new CloudVariantTreeItemViewModel(
            "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf",
            "tiny",
            isBase: false,
            "summary");
        source.IsExpanded = true;
        created.IsSelected = true;

        VariantTreeSelection.ExpandOnly(new[] { source, created }, created);
        Assert.False(source.IsExpanded);
        Assert.True(created.IsExpanded);
        Assert.Same(created, VariantTreeSelection.ResolveActive(new[] { source, created }, created));
        Assert.False(VariantTreeSelection.ShouldReassert(created, created));
    }

        [Fact]
        public void Save_keeps_the_expanded_copy_even_if_the_default_row_is_also_expanded()
        {
            var defaults = new CloudVariantTreeItemViewModel(
                "Qwen3.8-27B-Q4_K_M.gguf",
                "(defaults)",
                isBase: true,
                "summary");
            var copy = new CloudVariantTreeItemViewModel(
                "Qwen3.8-27B-Q4_K_M.gguf",
                "vision",
                isBase: false,
                "summary");
            defaults.IsExpanded = true;
            copy.IsExpanded = true;
            copy.IsSelected = true;

            var active = VariantTreeSelection.ResolveActive(new[] { defaults, copy }, copy);
            Assert.Same(copy, active);

            VariantTreeSelection.ExpandOnly(new[] { defaults, copy }, copy);
            Assert.False(defaults.IsExpanded);
            Assert.True(copy.IsExpanded);
        }

    [Fact]
    public void ExpandOnly_does_not_retoggle_a_row_that_is_already_open()
    {
        var copy = new CloudVariantTreeItemViewModel(
            "Qwen3.8-27B-Q4_K_M.gguf",
            "vision",
            isBase: false,
            "summary");
        copy.IsExpanded = true;
        var expandedChanges = 0;
        copy.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CloudVariantTreeItemViewModel.IsExpanded))
            {
                expandedChanges++;
            }
        };

        VariantTreeSelection.ExpandOnly(new[] { copy }, copy);
        Assert.True(copy.IsExpanded);
        Assert.Equal(0, expandedChanges);
    }

    [Fact]
    public void First_click_on_a_closed_row_does_not_snap_back_to_the_open_row()
    {
        var open = new CloudVariantTreeItemViewModel(
            "Qwen3.8-27B-Q4_K_M.gguf",
            "(defaults)",
            isBase: true,
            "summary");
        var closed = new CloudVariantTreeItemViewModel(
            "Qwen3.8-27B-Q4_K_M.gguf",
            "vision",
            isBase: false,
            "summary");
        open.IsExpanded = true;
        closed.IsSelected = true;

        Assert.False(VariantTreeSelection.ShouldAdoptStolenSelection(closed, open));
        Assert.True(VariantTreeSelection.ShouldAdoptStolenSelection(null, open));
    }

    [Fact]
    public void Reassert_is_skipped_when_the_expanded_copy_is_already_selected()
    {
        var copy = new CloudVariantTreeItemViewModel(
            "Qwen3.8-27B-Q4_K_M.gguf",
            "vision",
            isBase: false,
            "summary");
        copy.IsExpanded = true;
        copy.IsSelected = true;

        Assert.False(VariantTreeSelection.ShouldReassert(copy, copy));
    }

    [Fact]
    public void Reassert_is_needed_when_selection_jumped_off_the_expanded_copy()
    {
        var defaults = new CloudVariantTreeItemViewModel(
            "Qwen3.8-27B-Q4_K_M.gguf",
            "(defaults)",
            isBase: true,
            "summary");
        var copy = new CloudVariantTreeItemViewModel(
            "Qwen3.8-27B-Q4_K_M.gguf",
            "vision",
            isBase: false,
            "summary");
        copy.IsExpanded = true;

        Assert.True(VariantTreeSelection.ShouldReassert(defaults, copy));
    }

    [Fact]
    public void ResolveExpanded_does_not_open_a_collapsed_cloud_selection()
    {
        var cloud = new CloudVariantTreeItemViewModel(
            "gpt-4.1",
            "(defaults)",
            isBase: true,
            "summary",
            provider: "OpenAI");
        var local = new CloudVariantTreeItemViewModel(
            "Qwen3.8-27B-Q4_K_M.gguf",
            "vision",
            isBase: false,
            "summary");
        cloud.IsSelected = true;
        local.IsExpanded = true;

        Assert.Null(VariantTreeSelection.ResolveExpanded(new[] { cloud }, cloud));
        Assert.Same(local, VariantTreeSelection.ResolveExpanded(new[] { local }, cloud));
    }

    [Fact]
    public void CollapseAll_closes_every_row_so_the_other_tree_can_own_the_editor()
    {
        var cloud = new CloudVariantTreeItemViewModel(
            "gpt-4.1",
            "(defaults)",
            isBase: true,
            "summary",
            provider: "OpenAI");
        var local = new CloudVariantTreeItemViewModel(
            "Qwen3.8-27B-Q4_K_M.gguf",
            "vision",
            isBase: false,
            "summary");
        cloud.IsExpanded = true;
        local.IsExpanded = true;

        VariantTreeSelection.CollapseAll(new[] { cloud });
        VariantTreeSelection.ExpandOnly(new[] { local }, local);

        Assert.False(cloud.IsExpanded);
        Assert.True(local.IsExpanded);
        Assert.Null(VariantTreeSelection.ResolveExpanded(new[] { cloud }, cloud));
    }

        [Fact]
        public void Copy_is_editable_while_expanded_even_if_not_selected()
    {
        var item = new CloudVariantTreeItemViewModel(
            "Qwen3.8-27B-Q8_0.gguf",
            "vision",
            isBase: false,
            "summary");

        Assert.True(item.IsEditable);
        Assert.False(item.CanEditSettings);

        item.IsExpanded = true;
        Assert.True(item.CanEditSettings);
        Assert.True(item.ShowEditingMarker);

        item.IsExpanded = false;
        item.IsSelected = true;
        Assert.True(item.CanEditSettings);
        Assert.False(item.ShowEditingMarker);
    }

    [Fact]
    public void Missing_model_id_default_can_be_deleted_without_unlock()
    {
        var item = new CloudVariantTreeItemViewModel(
            "",
            "(defaults)",
            isBase: true,
            "summary",
            provider: "Copilot GitHub");

        Assert.Equal("Missing model id (default)", item.DisplayName);
        Assert.False(item.CanDeleteProfile);

        item.IsExpanded = true;
        Assert.True(item.CanDeleteProfile);
        Assert.False(item.IsEditable);
    }

    [Fact]
    public void Locked_default_stays_read_only_when_expanded()
    {
        var item = new CloudVariantTreeItemViewModel(
            "Qwen3.8-27B-Q8_0.gguf",
            "(defaults)",
            isBase: true,
            "summary");

        item.IsExpanded = true;
        item.IsSelected = true;
        Assert.False(item.IsEditable);
        Assert.False(item.CanEditSettings);
        Assert.False(item.CanDeleteProfile);
        Assert.False(item.ShowEditingMarker);
    }

    [Fact]
    public void Unlocked_default_can_be_edited_and_deleted_when_expanded()
    {
        var item = new CloudVariantTreeItemViewModel(
            "Qwen3.8-27B-Q8_0.gguf",
            "(defaults)",
            isBase: true,
            "summary",
            isUnlocked: true);

        item.IsExpanded = true;
        Assert.True(item.IsEditable);
        Assert.True(item.CanEditSettings);
        Assert.True(item.CanDeleteProfile);
        Assert.True(item.ShowEditingMarker);
    }

    [Fact]
    public void Validated_profile_shows_green_check_when_saved_settings_match()
    {
        var item = new CloudVariantTreeItemViewModel(
            "Qwen3.8-27B-Q8_0.gguf",
            "Vision",
            isBase: false,
            "summary",
            isValidated: true);

        Assert.True(item.ShowValidatedCheck);
        Assert.False(item.ShowMissingFileWarning);
        Assert.Equal(
            "Validated. Latest saved settings match that test. Listed in the Quick Select dropdown.",
            item.EndpointStatusText);
    }

    [Fact]
    public void Warning_hides_validated_check()
    {
        const string savedAfterValidate =
            "Settings were saved after the last successful Validate. Run Validate to endpoint again.";
        var item = new CloudVariantTreeItemViewModel(
            "Qwen3.8-27B-Q8_0.gguf",
            "Vision",
            isBase: false,
            "summary",
            isValidated: true,
            warningTooltip: savedAfterValidate);

        Assert.True(item.ShowMissingFileWarning);
        Assert.False(item.ShowValidatedCheck);
        Assert.Equal(savedAfterValidate, item.EndpointStatusText);

        item.ApplyEndpointStatus(
            showWarning: false,
            warningTooltip: string.Empty,
            isValidatedOnDisk: true,
            hasUnsavedEdits: true);
        Assert.False(item.ShowValidatedCheck);
        Assert.Equal(
            "Unsaved edits. Last Validate matches the last Save, not these on-screen changes.",
            item.EndpointStatusText);
    }
}
