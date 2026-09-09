using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FluxMux.Avalonia.Services;

public readonly record struct DeepSeekHarnessSetupOptions(
    int FluxMuxPort,
    int ContextWindow,
    int MaxTokens,
    bool ImagesOn,
    bool ReasoningOn,
    string ModelDisplayName)
{
    public static DeepSeekHarnessSetupOptions Create(
        int fluxMuxPort,
        int contextWindow,
        int maxTokens = 4096,
        bool imagesOn = false,
        bool reasoningOn = false,
        string? modelDisplayName = null)
    {
        var port = Math.Clamp(fluxMuxPort, 1, 65535);
        var context = Math.Clamp(contextWindow, 1024, DeepSeekHarnessSetup.MaxLocalContextWindow);
        var max = Math.Clamp(maxTokens, 256, 131072);
        var name = string.IsNullOrWhiteSpace(modelDisplayName) ? "local" : modelDisplayName.Trim();
        return new DeepSeekHarnessSetupOptions(port, context, max, imagesOn, reasoningOn, name);
    }
}

public readonly record struct DeepSeekHarnessSettingsMergeResult(
    bool Success,
    bool CreatedNewFile,
    bool BackedUpExistingFile,
    string SettingsPath,
    string Message);

public static class DeepSeekHarnessSetup
{
    public const int MaxLocalContextWindow = 262144;
    public const int DefaultWebPort = 3080;
    public const string ProviderId = "ai-fluxmux";
    public const string ApiKeyEnvVar = "AI_FLUXMUX_API_KEY";
    public const string DummyApiKey = "sk-local";
    public const string InstallDocsUrl = "https://deepseek-harness.github.io/deepseek-harness/";
    public const string InstallGitHubUrl = "https://github.com/deepseek-ai/deepseek-harness#run-from-npm";
    private static readonly HttpClient ProbeClient = new() { Timeout = TimeSpan.FromSeconds(2) };

    public static string ResolveSettingsPath()
        => Path.Combine(ResolveDshHomeDirectory(), "settings.yaml");

    public static string ResolveDshHomeDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dsh");

    public static string ResolveEnvFilePath()
        => Path.Combine(ResolveDshHomeDirectory(), ".env");

    public static string ResolveHarnessPidFilePath()
        => Path.Combine(ResolveDshHomeDirectory(), "fluxmux-harness.pid");

    public static string BuildApiKeyEnvLine()
        => ApiKeyEnvVar + "=" + DummyApiKey;

    public static string MergeEnvFileContent(string? existing, string varName, string value)
    {
        var targetLine = varName + "=" + value;
        if (string.IsNullOrWhiteSpace(existing))
        {
            return targetLine + Environment.NewLine;
        }

        var lines = existing.Replace("\r\n", "\n").Split('\n').ToList();
        var prefix = varName + "=";
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = targetLine;
                return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
            }
        }

        lines.Add(targetLine);
        return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
    }

    public static bool EnsureFluxMuxApiKeyEnvFile()
    {
        try
        {
            var directory = ResolveDshHomeDirectory();
            Directory.CreateDirectory(directory);
            var path = ResolveEnvFilePath();
            var existing = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
            var merged = MergeEnvFileContent(existing, ApiKeyEnvVar, DummyApiKey);
            if (merged.Equals(existing, StringComparison.Ordinal))
            {
                return false;
            }

            File.WriteAllText(path, merged, Encoding.UTF8);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string BuildChatUrl(int webPort)
        => $"http://127.0.0.1:{Math.Clamp(webPort, 1, 65535)}";

    public static bool TryParseWebAuthUrl(string? text, int webPort, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var port = Math.Clamp(webPort, 1, 65535).ToString(CultureInfo.InvariantCulture);
        var match = System.Text.RegularExpressions.Regex.Match(
            text,
            @"https?://(?:127\.0\.0\.1|localhost):" + port + @"/?\?token=[A-Za-z0-9_\-]+",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }

        url = match.Value;
        return true;
    }

    public static string ResolveOpenChatUrl(int webPort)
        => DeepSeekHarnessWebHost.TryReadWebAuthUrlFromLaunchLog(webPort, out var authUrl)
            ? authUrl
            : BuildChatUrl(webPort);

    public static bool IsWebUiListeningStatus(int statusCode)
        => (statusCode >= 200 && statusCode < 300) || statusCode == 401 || statusCode == 403;

    public static bool WebUiRequiresAuthToken(int statusCode)
        => statusCode == 401;

    public static bool NeedsFreshWebAuth(int statusCode, bool haveAuthUrl)
        => WebUiRequiresAuthToken(statusCode) && !haveAuthUrl;

    public static bool IsWebUiReadyToOpen(int statusCode, bool haveAuthUrl)
        => (statusCode >= 200 && statusCode < 300)
           || (WebUiRequiresAuthToken(statusCode) && haveAuthUrl);

    public static string BuildModelBaseUrl(int fluxMuxPort)
        => $"http://127.0.0.1:{Math.Clamp(fluxMuxPort, 1, 65535)}/v1";

    /// <summary>
    /// DualHot packing stays on the live local (plus any larger validated
    /// local). A hot cloud hop may rewrite the yaml display name only.
    /// Harness already packed this session; that rewrite is for the next chat.
    /// </summary>
    public static bool YamlPackingFollowsLiveLocal(bool localAlive)
        => localAlive;

    public static bool ShouldAdvertiseCapability(bool loadedHasIt, bool offerableHasIt)
        => loadedHasIt || offerableHasIt;

    public static bool ShouldAdvertiseImages(bool loadedProfileImagesOn, bool visionProfileAvailable)
        => ShouldAdvertiseCapability(loadedProfileImagesOn, visionProfileAvailable);

    public static int AdvertiseNumericCapacity(int loaded, int offerableMax)
        => Math.Max(loaded, offerableMax);

    public static string BuildSettingsYaml(DeepSeekHarnessSetupOptions options)
    {
        var inputTypes = options.ImagesOn ? "[text, image]" : "[text]";
        var modelName = EscapeYamlScalar(options.ModelDisplayName);
        var modelLines = new List<string>
        {
            "        - id: local",
            "          name: " + modelName,
            $"          input: {inputTypes}",
            $"          contextWindow: {options.ContextWindow.ToString(CultureInfo.InvariantCulture)}",
            $"          maxTokens: {options.MaxTokens.ToString(CultureInfo.InvariantCulture)}"
        };
        if (!options.ReasoningOn)
        {
            modelLines.Add("          reasoningEfforts: false");
        }

        return string.Join(
            Environment.NewLine,
            new[]
            {
                "llm-pi-ai:",
                "  providers:",
                $"    {ProviderId}:",
                "      displayName: AI-FluxMux",
                "      api: openai-completions",
                $"      baseURL: {BuildModelBaseUrl(options.FluxMuxPort)}",
                "      apiKeyEnv: AI_FLUXMUX_API_KEY",
                $"      defaultContextWindow: {options.ContextWindow.ToString(CultureInfo.InvariantCulture)}",
                $"      defaultMaxTokens: {options.MaxTokens.ToString(CultureInfo.InvariantCulture)}",
                $"      defaultInput: {inputTypes}",
                "      compat:",
                "        supportsDeveloperRole: false",
                "        maxTokensField: max_tokens",
                "      models:"
            }.Concat(modelLines).Concat(new[]
            {
                "agent-default-model:",
                $"  provider: {ProviderId}",
                "  model: local"
            }));
    }

    public static string EscapeYamlScalar(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        var needsQuotes = value.Any(ch => char.IsWhiteSpace(ch) || ch == ':' || ch == '#' || ch == '\'');
        if (!needsQuotes)
        {
            return value;
        }

        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    public static DeepSeekHarnessSettingsMergeResult MergeIntoSettingsFile(DeepSeekHarnessSetupOptions options)
    {
        var path = ResolveSettingsPath();
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return new DeepSeekHarnessSettingsMergeResult(
                false,
                false,
                false,
                path,
                "Could not resolve the DeepSeek Harness settings folder.");
        }

        try
        {
            Directory.CreateDirectory(directory);
            var block = BuildSettingsYaml(options);
            if (!File.Exists(path))
            {
                File.WriteAllText(path, block + Environment.NewLine, Encoding.UTF8);
                EnsureFluxMuxApiKeyEnvFile();
                return new DeepSeekHarnessSettingsMergeResult(
                    true,
                    true,
                    false,
                    path,
                    "Harness settings were created for the loaded local profile capacity.");
            }

            var existing = File.ReadAllText(path, Encoding.UTF8);
            var backedUp = false;
            if (!string.IsNullOrWhiteSpace(existing))
            {
                File.WriteAllText(path + ".bak", existing, Encoding.UTF8);
                backedUp = true;
            }

            var merged = MergeProviderBlock(existing, block);
            File.WriteAllText(path, merged.TrimEnd() + Environment.NewLine, Encoding.UTF8);
            EnsureFluxMuxApiKeyEnvFile();
            return new DeepSeekHarnessSettingsMergeResult(
                true,
                false,
                backedUp,
                path,
                backedUp
                    ? "Harness settings were updated for the loaded local profile capacity (backup: settings.yaml.bak)."
                    : "Harness settings were updated for the loaded local profile capacity.");
        }
        catch (Exception ex)
        {
            return new DeepSeekHarnessSettingsMergeResult(
                false,
                false,
                false,
                path,
                "Could not write Harness settings: " + ex.Message);
        }
    }

    public static string MergeProviderBlock(string existing, string block)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return block;
        }

        var providerBody = ExtractYamlSection(block, $"    {ProviderId}:");
        var defaultBody = ExtractYamlSection(block, "agent-default-model:");
        var cleaned = existing;
        while (true)
        {
            var next = RemoveYamlSection(cleaned, $"    {ProviderId}:");
            if (next.Equals(cleaned, StringComparison.Ordinal))
            {
                break;
            }

            cleaned = next;
        }

        while (true)
        {
            var next = RemoveYamlSection(cleaned, "agent-default-model:");
            if (next.Equals(cleaned, StringComparison.Ordinal))
            {
                break;
            }

            cleaned = next;
        }

        cleaned = CollapseBlankLines(cleaned.TrimEnd());
        if (cleaned.Contains("llm-pi-ai:", StringComparison.Ordinal)
            && cleaned.Contains("  providers:", StringComparison.Ordinal))
        {
            cleaned = InsertProviderAfterProvidersLine(cleaned, providerBody);
            return cleaned + Environment.NewLine + Environment.NewLine + defaultBody + Environment.NewLine;
        }

        return cleaned
            + Environment.NewLine
            + Environment.NewLine
            + block
            + Environment.NewLine;
    }

    internal static string InsertProviderAfterProvidersLine(string yaml, string providerBody)
    {
        if (string.IsNullOrWhiteSpace(providerBody))
        {
            return yaml;
        }

        var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();
        var insertAt = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].TrimEnd().Equals("  providers:", StringComparison.Ordinal))
            {
                insertAt = i + 1;
                break;
            }
        }

        if (insertAt < 0)
        {
            return yaml + Environment.NewLine + providerBody;
        }

        var providerLines = providerBody.Split('\n');
        for (var i = providerLines.Length - 1; i >= 0; i--)
        {
            var line = providerLines[i].TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            lines.Insert(insertAt, line);
        }

        return string.Join(Environment.NewLine, lines);
    }

    internal static string CollapseBlankLines(string yaml)
    {
        while (yaml.Contains("\n\n\n", StringComparison.Ordinal))
        {
            yaml = yaml.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
        }

        while (yaml.Contains("\r\n\r\n\r\n", StringComparison.Ordinal))
        {
            yaml = yaml.Replace("\r\n\r\n\r\n", "\r\n\r\n", StringComparison.Ordinal);
        }

        return yaml;
    }

    internal static string ExtractYamlSection(string yaml, string marker)
    {
        var lines = yaml.Replace("\r\n", "\n").Split('\n');
        var markerIndent = CountLeadingSpaces(marker);
        var start = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd().Equals(marker.TrimEnd(), StringComparison.Ordinal))
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i];
            if (i > start && line.Trim().Length > 0)
            {
                var indent = CountLeadingSpaces(line);
                if (indent < markerIndent
                    || (indent == markerIndent && !line.TrimStart().StartsWith('-')))
                {
                    break;
                }
            }

            builder.AppendLine(line.TrimEnd('\r'));
        }

        return builder.ToString().TrimEnd();
    }

    internal static string RemoveYamlSection(string yaml, string marker)
    {
        var section = ExtractYamlSection(yaml, marker);
        if (string.IsNullOrWhiteSpace(section))
        {
            return yaml;
        }

        return yaml.Replace(section, string.Empty, StringComparison.Ordinal)
            .Replace("\r\n\r\n\r\n", "\r\n\r\n", StringComparison.Ordinal)
            .Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
    }

    private static int CountLeadingSpaces(string line)
    {
        var count = 0;
        foreach (var ch in line)
        {
            if (ch == ' ')
            {
                count++;
                continue;
            }

            if (ch == '\t')
            {
                count += 4;
                continue;
            }

            break;
        }

        return count;
    }

    public static async Task<int> ProbeWebUiStatusAsync(int webPort, CancellationToken cancellationToken = default)
    {
        var url = BuildChatUrl(webPort);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await ProbeClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return (int)response.StatusCode;
        }
        catch
        {
            return 0;
        }
    }

    public static async Task<bool> ProbeWebUiReachableAsync(int webPort, CancellationToken cancellationToken = default)
        => IsWebUiListeningStatus(await ProbeWebUiStatusAsync(webPort, cancellationToken).ConfigureAwait(false));

    public static async Task<bool> ProbeModelApiReachableAsync(int fluxMuxPort, CancellationToken cancellationToken = default)
    {
        var url = BuildModelBaseUrl(fluxMuxPort) + "/models";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await ProbeClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public static int ResolveNumericSetting(string? value, int fallback)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Provider default", StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : fallback;
    }
}
