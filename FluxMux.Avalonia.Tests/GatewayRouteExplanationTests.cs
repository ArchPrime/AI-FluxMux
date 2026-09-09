using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class GatewayRouteExplanationTests
{
    [Fact]
    public void FormatLine_includes_route_overlay_compact_reload_and_request_id()
    {
        var line = GatewayRouteExplanation.FormatLine("local", "coding", true, true, false, "abc12345");
        Assert.Equal("local · overlay coding · compact yes · reload offered · req abc12345", line);
    }

    [Fact]
    public void FormatLine_marks_cloud_consent_route()
    {
        var line = GatewayRouteExplanation.FormatLine("local", null, false, false, true, "deadbeef");
        Assert.Equal("cloud-consent · overlay none · compact no · reload none · req deadbeef", line);
    }

    [Fact]
    public void Parse_reads_saved_json_snapshot()
    {
        var json = GatewayRouteExplanation.ToJson("cloud", "fast", false, false, false, "req1", "OpenAI", "gpt-4");
        var snapshot = GatewayRouteExplanation.Parse(json);
        Assert.NotNull(snapshot);
        Assert.Contains("cloud", snapshot.Value.Line, System.StringComparison.Ordinal);
        Assert.Equal("req1", snapshot.Value.RequestId);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Value.UpdatedUtc));
    }
}
