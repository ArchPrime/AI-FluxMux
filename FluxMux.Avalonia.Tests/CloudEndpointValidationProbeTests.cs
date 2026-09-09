using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class CloudEndpointValidationProbeTests
{
    [Fact]
    public void Probe_omits_temperature_and_uses_a_thinking_sized_reply_budget()
    {
        var payload = CloudEndpointValidationProbe.CreatePayload("gemini-flash-lite-latest");

        Assert.Equal("gemini-flash-lite-latest", payload["model"]?.ToString());
        Assert.Null(payload["temperature"]);
        Assert.Equal(CloudEndpointValidationProbe.MaxTokens, payload["max_tokens"]?.GetValue<int>());
        Assert.True(CloudEndpointValidationProbe.MaxTokens >= 256);
        Assert.Equal(false, payload["stream"]?.GetValue<bool>());
        Assert.Equal(
            CloudEndpointValidationProbe.UserText,
            payload["messages"]?[0]?["content"]?.ToString());
    }

    [Fact]
    public void Force_route_header_is_the_same_name_local_and_cloud_probes_use()
    {
        Assert.Equal("X-FluxMux-Force-Route", CloudEndpointValidationProbe.ForceRouteHeader);
        Assert.False(FluxMuxGatewayRouting.AllowsOperatorRouting(FluxMuxGatewayRouting.Local));
        Assert.False(FluxMuxGatewayRouting.AllowsOperatorRouting(FluxMuxGatewayRouting.Cloud));
    }
}
