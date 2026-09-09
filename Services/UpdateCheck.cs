using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FluxMux.Avalonia.Services;

public sealed class UpdateCheckResult
{
    public bool AppUpdateAvailable { get; init; }
    public bool HelpReplaced { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
}

/// <summary>
/// Reads a small JSON feed you host later. No repository is required until that URL exists.
/// A newer app version is announced; Help.html can be replaced beside AI-FluxMux without a rebuild.
/// </summary>
public static class UpdateCheck
{
    public static bool TryValidateFeedUrl(string? feedUrl, out Uri? uri, out string error)
    {
        uri = null;
        error = string.Empty;
        var text = feedUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "No feed URL yet. Paste one when you have a public host. Until then, you can still replace Help.html next to AI-FluxMux.exe by hand.";
            return false;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
        {
            error = "The update feed URL must be a full http or https address.";
            return false;
        }

        uri = parsed;
        return true;
    }

    public static async Task<UpdateCheckResult> RunAsync(
        HttpClient http,
        string? feedUrl,
        string currentVersion,
        string helpFilePath,
        string? fallbackHelpFilePath = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateFeedUrl(feedUrl, out var feedUri, out var error) || feedUri is null)
        {
            return new UpdateCheckResult { StatusText = error };
        }

        string json;
        try
        {
            json = await http.GetStringAsync(feedUri, cancellationToken);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult { StatusText = "Could not read the update feed: " + ex.Message };
        }

        if (!UpdateManifestParser.TryParse(json, feedUri, out var manifest, out var parseError) || manifest is null)
        {
            return new UpdateCheckResult { StatusText = parseError };
        }

        var appNewer = AppVersionComparer.Compare(currentVersion, manifest.Version) < 0;
        var helpReplaced = false;
        if (!string.IsNullOrWhiteSpace(manifest.HelpUrl))
        {
            var helpResult = await TryReplaceHelpAsync(
                http,
                manifest,
                helpFilePath,
                fallbackHelpFilePath,
                cancellationToken);
            if (!helpResult.ok && !string.IsNullOrWhiteSpace(helpResult.error))
            {
                return new UpdateCheckResult
                {
                    AppUpdateAvailable = appNewer,
                    DownloadUrl = appNewer ? manifest.DownloadUrl : string.Empty,
                    StatusText = helpResult.error
                };
            }

            helpReplaced = helpResult.replaced;
        }

        return new UpdateCheckResult
        {
            AppUpdateAvailable = appNewer,
            HelpReplaced = helpReplaced,
            DownloadUrl = appNewer ? manifest.DownloadUrl : string.Empty,
            StatusText = BuildStatus(currentVersion, manifest, appNewer, helpReplaced)
        };
    }

    public static string Sha256Hex(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static async Task<(bool ok, bool replaced, string error)> TryReplaceHelpAsync(
        HttpClient http,
        UpdateManifest manifest,
        string helpFilePath,
        string? fallbackHelpFilePath,
        CancellationToken cancellationToken)
    {
        byte[] downloaded;
        try
        {
            downloaded = await http.GetByteArrayAsync(new Uri(manifest.HelpUrl), cancellationToken);
        }
        catch (Exception ex)
        {
            return (false, false, "Could not download Help.html: " + ex.Message);
        }

        if (downloaded.Length == 0)
        {
            return (false, false, "The Help.html from the feed was empty.");
        }

        var downloadedHash = Sha256Hex(downloaded);
        if (!string.IsNullOrWhiteSpace(manifest.HelpSha256)
            && !downloadedHash.Equals(manifest.HelpSha256, StringComparison.OrdinalIgnoreCase))
        {
            return (false, false, "The Help.html from the feed did not match helpSha256. It was not saved.");
        }

        byte[]? local = null;
        if (File.Exists(helpFilePath))
        {
            local = await File.ReadAllBytesAsync(helpFilePath, cancellationToken);
            if (Sha256Hex(local).Equals(downloadedHash, StringComparison.OrdinalIgnoreCase))
            {
                return (true, false, string.Empty);
            }
        }

        // An installed AI-FluxMux cannot write beside its own exe, so the update goes to
        // the per-user copy instead. The Help tab already loads whichever copy is newest.
        try
        {
            await SaveHelpAsync(helpFilePath, downloaded, cancellationToken);
        }
        catch (Exception ex)
        {
            if (string.IsNullOrWhiteSpace(fallbackHelpFilePath)
                || PathsMatch(fallbackHelpFilePath, helpFilePath))
            {
                return (false, false, "Could not save Help.html: " + ex.Message);
            }

            try
            {
                await SaveHelpAsync(fallbackHelpFilePath, downloaded, cancellationToken);
            }
            catch (Exception fallbackEx)
            {
                return (false, false, "Could not save Help.html: " + fallbackEx.Message);
            }
        }

        return (true, true, string.Empty);
    }

    private static async Task SaveHelpAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, content, cancellationToken);
            File.Copy(tempPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception)
            {
                // A leftover temp file must not turn a saved update into a failure.
            }
        }
    }

    private static bool PathsMatch(string left, string right)
    {
        try
        {
            return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string BuildStatus(string currentVersion, UpdateManifest manifest, bool appNewer, bool helpReplaced)
    {
        var notes = string.IsNullOrWhiteSpace(manifest.Notes) ? string.Empty : " " + manifest.Notes;
        if (appNewer && helpReplaced)
        {
            return "A newer AI-FluxMux is available (" + manifest.Version + ")." + notes
                + " Help.html was updated. Open the download page for the app.";
        }

        if (appNewer)
        {
            return "A newer AI-FluxMux is available (" + manifest.Version + ")." + notes
                + (string.IsNullOrWhiteSpace(manifest.DownloadUrl)
                    ? string.Empty
                    : " Open the download page.");
        }

        if (helpReplaced)
        {
            return "This AI-FluxMux is current (" + currentVersion + "). Help.html was updated from the feed.";
        }

        return "This AI-FluxMux is current (" + currentVersion + "). Help.html matches the feed.";
    }
}
