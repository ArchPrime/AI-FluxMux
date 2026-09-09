using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace FluxMux.Avalonia.Services;

public static class LocalHealthGuidance
{
    private static readonly Regex OverflowRegex = new(
        @"request tokens (?<used>[\d,]+) exceeded context (?<ctx>[\d,]+) by (?<over>[\d,]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WhitespaceRegex = new(
        @"[\s\u00a0]+",
        RegexOptions.Compiled);

    public static bool IsVisionProjectorFile(string? pathOrFile)
    {
        var name = Path.GetFileName(pathOrFile ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.Contains("mmproj", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".mmproj", StringComparison.OrdinalIgnoreCase);
    }

    public static string FormatVisionProjectorAsModelWarning(string? pathOrFile)
    {
        return LocalModelName(pathOrFile)
            + " is a vision projector (mmproj), not a local model. Create a model profile for the matching GGUF, turn Images on, and choose this projector there.";
    }

    /// <summary>
    /// One-line profile-panel status. Raw llama-server tails stay in Diagnostics.
    /// </summary>
    public static string FormatProfilePanelWarning(string? details)
    {
        var text = CollapseWhitespace(details);
        if (string.IsNullOrWhiteSpace(text))
        {
            return "The last connection attempt failed. Run Validate to endpoint again to clear this warning.";
        }

        if (text.Contains("vision projector (mmproj)", StringComparison.OrdinalIgnoreCase))
        {
            return TrimStatus(text);
        }

        if (text.Contains("exited during startup", StringComparison.OrdinalIgnoreCase))
        {
            return "llama-server exited during startup. Open Diagnostics for Last llama-server output.";
        }

        var lastOutputAt = text.IndexOf("Last llama-server output:", StringComparison.OrdinalIgnoreCase);
        if (lastOutputAt > 0)
        {
            var prefix = text[..lastOutputAt].Trim();
            if (prefix.Length > 0)
            {
                return TrimStatus(prefix + " Open Diagnostics for Last llama-server output.");
            }
        }

        return TrimStatus(text);
    }

    private static string CollapseWhitespace(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return string.Empty;
        }

        return WhitespaceRegex.Replace(details.Trim(), " ");
    }

    private static string TrimStatus(string text)
    {
        if (text.Length <= 180)
        {
            return text;
        }

        return text[..177] + "...";
    }

    public static string Build(
        bool ready,
        bool managedAlive,
        bool portOpen,
        int probePort,
        int publicPort,
        int? managedPid,
        string? overflow,
        string? stderrTail,
        string? localModel = null)
    {
        _ = probePort;
        _ = managedPid;
        _ = portOpen;

        var model = LocalModelName(localModel);

        if (ready)
        {
            if (string.IsNullOrWhiteSpace(overflow))
            {
                return "llama-server is up with " + model
                    + ". The Client app should use the AI-FluxMux address http://127.0.0.1:"
                    + publicPort.ToString(CultureInfo.InvariantCulture) + ".";
            }

            return FormatOverflow(overflow);
        }

        if (managedAlive)
        {
            var stuck = "llama-server is still starting. Wait until " + model
                + " finishes loading. If this stays for several minutes, "
                + ControlLabelMarkup.Mark("Stop")
                + " and "
                + ControlLabelMarkup.Mark("Launch")
                + " this model profile again. If the llama-server log mentions out of memory, lower Context or GPU layers on this model profile, "
                + ControlLabelMarkup.Mark("Save profile")
                + ", then "
                + ControlLabelMarkup.Mark("Launch")
                + " again.";
            return AppendStderr(stuck, stderrTail);
        }

        var down = "llama-server is not answering. "
            + ControlLabelMarkup.Mark("Launch")
            + " the local model profile again from Quick Select (or "
            + ControlLabelMarkup.Mark("Validate to endpoint")
            + " on Model Profiles). The Client app should use the AI-FluxMux address http://127.0.0.1:"
            + publicPort.ToString(CultureInfo.InvariantCulture) + ".";
        return AppendStderr(down, stderrTail);
    }

    public static string BuildPublicGatewayMiss(string localDetails, int publicPort)
    {
        var prefix = string.IsNullOrWhiteSpace(localDetails)
            ? "llama-server is up."
            : localDetails.Trim();
        return prefix
            + " The AI-FluxMux public address http://127.0.0.1:"
            + publicPort.ToString(CultureInfo.InvariantCulture)
            + " did not reply to a short test. Point the Client app at that URL. Click "
            + ControlLabelMarkup.Mark("Health")
            + " again; if it still fails, relaunch the local model profile so AI-FluxMux restarts that address.";
    }

    public const string HarnessWebDownHeadline =
        "Health needs attention: Harness web is not running.";

    public static string BuildHarnessWebDown()
        => "Harness web is not running. The Harness chat page cannot send turns (a Failed to fetch error is that page, not Port). Click "
           + ControlLabelMarkup.Mark("Harness web chat")
           + " on the active Quick Select slot, and use the new tab.";

    public static string LocalModelName(string? localModel)
    {
        if (string.IsNullOrWhiteSpace(localModel))
        {
            return "the local model";
        }

        var name = Path.GetFileName(localModel.Trim());
        return string.IsNullOrWhiteSpace(name) ? "the local model" : name;
    }

    private static string FormatOverflow(string overflow)
    {
        var match = OverflowRegex.Match(overflow);
        if (match.Success)
        {
            var used = match.Groups["used"].Value;
            var ctx = match.Groups["ctx"].Value;
            var over = match.Groups["over"].Value;
            return "Problem: a recent local request did not fit Context. This request used about "
                + used + " tokens; the loaded model profile's Context is " + ctx + " (" + over + " over). "
                + "What to do: in Model Profiles, raise Context on this model profile (or load a model profile with a larger Context), then wait for llama-server to reload. You can also start a shorter chat.";
        }

        return "Problem: a recent local request did not fit Context. "
            + "What to do: in Model Profiles, raise Context on this model profile (or load a model profile with a larger Context), then wait for llama-server to reload. You can also start a shorter chat.";
    }

    private static string AppendStderr(string message, string? stderrTail)
    {
        if (string.IsNullOrWhiteSpace(stderrTail))
        {
            return message;
        }

        return message + " Last llama-server output: " + stderrTail.Trim();
    }
}
