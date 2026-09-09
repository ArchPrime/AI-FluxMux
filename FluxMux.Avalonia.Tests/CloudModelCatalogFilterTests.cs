using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class CloudModelCatalogFilterTests
{
    [Fact]
    public void Filter_removes_obvious_non_chat_ids_from_copilot_catalog()
    {
        var filtered = CloudModelCatalogFilter.Filter(
            "Copilot GitHub",
            [
                "gpt-4.1",
                "gemini-3.6-flash",
                "text-embedding-3-small",
                "text-embedding-3-small-inference",
                "exec-agent-a"
            ]);

        Assert.Equal(["gemini-3.6-flash", "gpt-4.1"], filtered);
    }

    [Fact]
    public void IsLikelyChatModelId_rejects_embedding_and_whisper_names()
    {
        Assert.False(CloudModelCatalogFilter.IsLikelyChatModelId("text-embedding-3-large"));
        Assert.True(CloudModelCatalogFilter.IsLikelyChatModelId("gpt-4.1"));
    }
}
