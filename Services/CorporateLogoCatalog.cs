using System;
using System.Text.RegularExpressions;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Maps a cloud provider or local GGUF/model id to the official company mark
/// bundled under Assets/Logos (lobe-icons brand SVGs rasterized as PNG).
/// </summary>
public static class CorporateLogoCatalog
{
    public readonly record struct Match(string AssetKey, string CompanyName);

    public static Match? Resolve(string? provider, string? modelName)
    {
        var providerText = (provider ?? string.Empty).Trim();
        if (providerText.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
        {
            return new Match("gemini-color", "Google Gemini");
        }

        if (providerText.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return new Match("claude-color", "Anthropic");
        }

        if (providerText.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            return new Match("openai", "OpenAI");
        }

        if (providerText.Equals("Copilot GitHub", StringComparison.OrdinalIgnoreCase))
        {
            return new Match("githubcopilot", "GitHub Copilot");
        }

        var haystack = $"{providerText} {modelName ?? string.Empty}";
        return MatchFromModelText(haystack);
    }

    private static Match? MatchFromModelText(string haystack)
    {
        if (LooksLike(haystack, "gemini"))
        {
            return new Match("gemini-color", "Google Gemini");
        }

        if (LooksLike(haystack, "claude", "anthropic"))
        {
            return new Match("claude-color", "Anthropic");
        }

        if (LooksLike(haystack, "githubcopilot", "copilot"))
        {
            return new Match("githubcopilot", "GitHub Copilot");
        }

        if (LooksLike(haystack, "gpt", "chatgpt", "openai") || LooksLike(haystack, "o1", "o3", "o4"))
        {
            return new Match("openai", "OpenAI");
        }

        if (LooksLike(haystack, "deepseek"))
        {
            return new Match("deepseek-color", "DeepSeek");
        }

        if (LooksLike(haystack, "qwen", "qwq"))
        {
            return new Match("qwen-color", "Alibaba Qwen");
        }

        if (LooksLike(haystack, "gemma"))
        {
            return new Match("gemma-color", "Google Gemma");
        }

        if (LooksLike(haystack, "llama", "codellama"))
        {
            return new Match("meta-color", "Meta Llama");
        }

        if (LooksLike(haystack, "mistral", "mixtral", "magistral"))
        {
            return new Match("mistral-color", "Mistral AI");
        }

        if (LooksLike(haystack, "phi"))
        {
            return new Match("microsoft-color", "Microsoft");
        }

        return null;
    }

    private static bool LooksLike(string haystack, params string[] needles)
    {
        foreach (var needle in needles)
        {
            var pattern = $@"(^|[^A-Za-z0-9]){Regex.Escape(needle)}(?![A-Za-z])";
            if (Regex.IsMatch(haystack, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return true;
            }
        }

        return false;
    }
}
