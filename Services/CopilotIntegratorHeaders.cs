using System;
using System.Collections.Generic;
using System.Net.Http;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Headers GitHub Copilot expects on integrator API calls (catalog and chat).
/// </summary>
public static class CopilotIntegratorHeaders
{
    private static readonly IReadOnlyDictionary<string, string> Headers =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Editor-Version"] = "vscode/1.96.0",
            ["Editor-Plugin-Version"] = "copilot-chat/0.24.0",
            ["Copilot-Integration-Id"] = "vscode-chat",
            ["User-Agent"] = "GitHubCopilotChat/0.24.0"
        };

    public static void Apply(HttpRequestMessage request)
    {
        foreach (var header in Headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }
}
