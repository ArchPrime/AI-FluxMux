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

    // ── New patterns added to fix the "need to hit launch a second time" issue ──

    [Fact]
    public void IsTransientLocalWarmupFailure_recognizes_TaskCanceledException_operation_canceled()
    {
        // .NET's TaskCanceledException (thrown when CancellationTokenSource.CancelAfter
        // fires) has the message "The operation was canceled." This is the most common
        // failure mode when the public endpoint probe times out.
        var details = "TaskCanceledException: The operation was canceled.";
        Assert.True(FluxMuxRuntimeService.IsTransientLocalWarmupFailure(details));
    }

    [Fact]
    public void IsTransientLocalWarmupFailure_recognizes_HttpRequestException_generic()
    {
        // Generic HttpRequestException with no specific message.
        var details = "HttpRequestException: An error occurred while sending the request.";
        Assert.True(FluxMuxRuntimeService.IsTransientLocalWarmupFailure(details));
    }

    [Fact]
    public void IsTransientLocalWarmupFailure_recognizes_HttpRequestException_type_name()
    {
        // Some exception messages include the type name.
        var details = "System.Net.Http.HttpRequestException: Connection refused (127.0.0.1:5001)";
        Assert.True(FluxMuxRuntimeService.IsTransientLocalWarmupFailure(details));
    }

    [Fact]
    public void IsTransientLocalWarmupFailure_recognizes_TaskCanceledException_type_name()
    {
        var details = "System.Threading.Tasks.TaskCanceledException: A task was canceled.";
        Assert.True(FluxMuxRuntimeService.IsTransientLocalWarmupFailure(details));
    }

    [Fact]
    public void IsTransientLocalWarmupFailure_recognizes_endpoint_not_responding()
    {
        var details = "Endpoint not responding on http://127.0.0.1:5001 yet; skipped chat probe to avoid waiting for an unstarted local server.";
        Assert.True(FluxMuxRuntimeService.IsTransientLocalWarmupFailure(details));
    }

    [Fact]
    public void IsTransientLocalWarmupFailure_recognizes_connection_reset()
    {
        var details = "HttpRequestException: connection reset by peer";
        Assert.True(FluxMuxRuntimeService.IsTransientLocalWarmupFailure(details));
    }

    [Fact]
    public void IsTransientLocalWarmupFailure_recognizes_forcibly_closed()
    {
        var details = "The response ended prematurely because the server closed the connection. forcibly closed";
        Assert.True(FluxMuxRuntimeService.IsTransientLocalWarmupFailure(details));
    }

    [Fact]
    public void IsTransientLocalWarmupFailure_still_ignores_auth_errors()
    {
        // Ensure the new patterns don't accidentally match auth errors.
        Assert.False(FluxMuxRuntimeService.IsTransientLocalWarmupFailure("HTTP 401 Unauthorized. invalid_api_key"));
        Assert.False(FluxMuxRuntimeService.IsTransientLocalWarmupFailure("HTTP 403 Forbidden. access_denied"));
    }

    [Fact]
    public void IsTransientLocalWarmupFailure_still_ignores_model_not_found()
    {
        // A model-not-found error is NOT transient — it won't fix itself by waiting.
        Assert.False(FluxMuxRuntimeService.IsTransientLocalWarmupFailure("HTTP 404 Not Found. model 'qwen3' not found"));
    }
}
