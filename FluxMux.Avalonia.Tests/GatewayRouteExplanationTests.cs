using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class GatewayRouteExplanationTests
{
    [Fact]
    public void Status_details_are_blank_when_no_model_is_loaded()
    {
        var line = GatewayRouteExplanation.FormatStatusDetails(
            modelLoaded: false,
            attributeSummary: "Images on · Reasoning Low · ctx 163,840",
            hasMatchingLastTurn: true,
            differingOverlayVariant: "vision 160K Context",
            compactApplied: false,
            reloadOffered: false,
            cloudConsent: false);
        Assert.Equal(string.Empty, line);
    }

    [Fact]
    public void Status_details_show_settings_and_matching_turn_notes_not_the_model_name()
    {
        var line = GatewayRouteExplanation.FormatStatusDetails(
            modelLoaded: true,
            attributeSummary: "Images on · Reasoning Low · ctx 163,840 · GPU only · temp 0.2 · max 32,768",
            hasMatchingLastTurn: true,
            differingOverlayVariant: null,
            compactApplied: false,
            reloadOffered: false,
            cloudConsent: false);
        Assert.Equal(
            "Images on · Reasoning Low · ctx 163,840 · GPU only · temp 0.2 · max 32,768 · Compact no",
            line);
    }

    [Fact]
    public void Status_details_omit_turn_notes_until_this_loaded_model_has_a_turn()
    {
        var line = GatewayRouteExplanation.FormatStatusDetails(
            modelLoaded: true,
            attributeSummary: "Images off · Reasoning off · ctx 131,072",
            hasMatchingLastTurn: false,
            differingOverlayVariant: "coding",
            compactApplied: true,
            reloadOffered: true,
            cloudConsent: true);
        Assert.Equal("Images off · Reasoning off · ctx 131,072", line);
    }

    [Fact]
    public void Status_details_drop_a_Cloud_name_prefix_already_on_the_row_above()
    {
        var line = GatewayRouteExplanation.FormatStatusDetails(
            modelLoaded: true,
            attributeSummary: "Cloud · Images on · Balanced · ctx Auto · temp 0.7 · max 8,192",
            hasMatchingLastTurn: false,
            differingOverlayVariant: null,
            compactApplied: false,
            reloadOffered: false,
            cloudConsent: false);
        Assert.Equal("Images on · Balanced · ctx Auto · temp 0.7 · max 8,192", line);
    }

    [Fact]
    public void Parse_keeps_structured_fields_from_an_old_saved_line()
    {
        var json = JsonNode.Parse("""
            {
              "line": "local · overlay vision 160K Context · compact no · reload none · req 37b5d480",
              "routeKind": "local",
              "overlayVariant": "vision 160K Context",
              "compactApplied": false,
              "reloadOffered": false,
              "cloudConsent": false,
              "requestId": "37b5d480",
              "model": "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf"
            }
            """)!.AsObject();

        var snapshot = GatewayRouteExplanation.Parse(json);
        Assert.NotNull(snapshot);
        Assert.Equal("Compact no", snapshot.Value.Line);
        Assert.Equal("vision 160K Context", snapshot.Value.OverlayVariant);
        Assert.Equal("Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf", snapshot.Value.Model);
        Assert.False(snapshot.Value.CompactApplied);
    }
}
