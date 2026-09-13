using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class ExternalProcessTextTests
{
    [Fact]
    public void RunOrEmpty_returns_empty_when_nvidia_smi_is_missing()
    {
        var output = ExternalProcessText.RunOrEmpty(
            "fluxmux-missing-nvidia-smi",
            "--query-gpu=index,name,memory.total,memory.used,driver_version --format=csv,noheader,nounits",
            1000);

        Assert.Equal(string.Empty, output);
    }
}
