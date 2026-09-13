using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalLaunchStartupRetryTests
{
    [Fact]
    public void ShouldRetry_a_first_empty_crash()
    {
        Assert.True(LocalLaunchStartupRetry.ShouldRetry(stderr: null, attemptNumber: 1));
        Assert.True(LocalLaunchStartupRetry.ShouldRetry(string.Empty, 1));
    }

    [Fact]
    public void ShouldRetry_a_cuda_busy_or_oom_tail()
    {
        Assert.True(LocalLaunchStartupRetry.ShouldRetry("CUDA error: out of memory", 1));
        Assert.True(LocalLaunchStartupRetry.ShouldRetry("ggml_backend_cuda: failed to allocate", 1));
    }

    [Fact]
    public void LooksTransientStartupFailure_matches_an_exit_during_startup()
    {
        Assert.True(LocalLaunchStartupRetry.LooksTransientStartupFailure(
            "llama-server exited during startup. Last llama-server output: CUDA"));
        Assert.False(LocalLaunchStartupRetry.LooksTransientStartupFailure(
            "error: unknown argument --not-a-flag"));
    }

    [Fact]
    public void ShouldNotRetry_a_second_attempt()
    {
        Assert.False(LocalLaunchStartupRetry.ShouldRetry("CUDA error: out of memory", 2));
    }

    [Theory]
    [InlineData("error: unknown argument --not-a-flag")]
    [InlineData("gguf_init_from_file: failed to open file")]
    [InlineData("failed to open 'missing.gguf': no such file")]
    public void ShouldNotRetry_a_permanent_launch_error(string stderr)
    {
        Assert.True(LocalLaunchStartupRetry.LooksPermanent(stderr));
        Assert.False(LocalLaunchStartupRetry.ShouldRetry(stderr, 1));
    }

    [Fact]
    public void VramLooksBusy_when_used_is_still_a_large_fraction()
    {
        Assert.True(LocalLaunchStartupRetry.VramLooksBusy(usedGiB: 20, totalGiB: 32));
        Assert.False(LocalLaunchStartupRetry.VramLooksBusy(usedGiB: 2, totalGiB: 32));
    }

    [Fact]
    public void VramLooksSettled_when_free_rises_after_a_kill()
    {
        Assert.True(LocalLaunchStartupRetry.VramLooksSettled(
            haveBefore: true,
            usedBeforeGiB: 22,
            freeBeforeGiB: 8,
            haveNow: true,
            usedNowGiB: 4,
            freeNowGiB: 26));
        Assert.False(LocalLaunchStartupRetry.VramLooksSettled(
            haveBefore: true,
            usedBeforeGiB: 22,
            freeBeforeGiB: 8,
            haveNow: true,
            usedNowGiB: 21.8,
            freeNowGiB: 8.1));
    }
}
