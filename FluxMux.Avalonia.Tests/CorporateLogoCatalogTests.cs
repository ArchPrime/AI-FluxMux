using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class CorporateLogoCatalogTests
{
    [Theory]
    [InlineData("Gemini", "gemini-2.5-flash", "gemini-color", "Google Gemini")]
    [InlineData("Anthropic", "claude-sonnet-4", "claude-color", "Anthropic")]
    [InlineData("OpenAI", "gpt-4.1", "openai", "OpenAI")]
    [InlineData("Copilot GitHub", "gpt-4o", "githubcopilot", "GitHub Copilot")]
    public void Cloud_provider_maps_to_that_company_mark(string provider, string model, string assetKey, string company)
    {
        var match = CorporateLogoCatalog.Resolve(provider, model);
        Assert.NotNull(match);
        Assert.Equal(assetKey, match.Value.AssetKey);
        Assert.Equal(company, match.Value.CompanyName);
    }

    [Theory]
    [InlineData("Qwen3.8-27B-Q4_K_M.gguf", "qwen-color", "Alibaba Qwen")]
    [InlineData("gemma-3-27b-it-Q4_K_M.gguf", "gemma-color", "Google Gemma")]
    [InlineData("Llama-3.1-8B-Instruct-Q4_K_M.gguf", "meta-color", "Meta Llama")]
    [InlineData("Mistral-Small-Instruct-2409-Q5_K_M.gguf", "mistral-color", "Mistral AI")]
    [InlineData("DeepSeek-R1-Distill-Qwen-32B-Q4_K_M.gguf", "deepseek-color", "DeepSeek")]
    [InlineData("Phi-4-Q4_K_M.gguf", "microsoft-color", "Microsoft")]
    public void Local_filename_maps_to_that_company_mark(string model, string assetKey, string company)
    {
        var match = CorporateLogoCatalog.Resolve(provider: string.Empty, model);
        Assert.NotNull(match);
        Assert.Equal(assetKey, match.Value.AssetKey);
        Assert.Equal(company, match.Value.CompanyName);
    }

    [Fact]
    public void DeepSeek_distill_of_Qwen_uses_DeepSeek_mark()
    {
        var match = CorporateLogoCatalog.Resolve(string.Empty, "DeepSeek-R1-Distill-Qwen-32B-Q4_K_M.gguf");
        Assert.Equal("deepseek-color", match?.AssetKey);
    }

    [Fact]
    public void Custom_OpenAI_compatible_id_is_inferred_from_the_model_name()
    {
        var match = CorporateLogoCatalog.Resolve("Custom OpenAI-Compatible", "claude-3-5-sonnet");
        Assert.Equal("claude-color", match?.AssetKey);
        Assert.Equal("Anthropic", match?.CompanyName);
    }

    [Fact]
    public void Unknown_local_file_has_no_logo()
    {
        Assert.Null(CorporateLogoCatalog.Resolve(string.Empty, "mystery-weights-Q4_K_M.gguf"));
    }
}
