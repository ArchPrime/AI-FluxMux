using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalLaunchWarmupProbeTests
{
    [Fact]
    public void IsTransientLocalWarmupFailure_recognizes_model_still_loading_503()
    {
        var details =
            "HTTP 503 Service Unavailable. llama-server is still loading the local model and is not ready for completions yet.";

        Assert.True(FluxMuxRuntimeService.IsTransientLocalWarmupFailure(details));
    }

    [Fact]
    public void IsTransientLocalWarmupFailure_ignores_unrelated_auth_errors()
    {
        Assert.False(FluxMuxRuntimeService.IsTransientLocalWarmupFailure("HTTP 401 Unauthorized. invalid_api_key"));
    }
}
