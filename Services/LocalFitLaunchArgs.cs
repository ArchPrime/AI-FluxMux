using System;
using System.Collections.Generic;

namespace FluxMux.Avalonia.Services;

public static class LocalFitLaunchArgs
{
    public static void Append(IList<string> args, string? localFit, string helpText)
    {
        var fit = (localFit ?? string.Empty).Trim();
        if (fit.Equals("Auto", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(fit))
        {
            return;
        }

        if (SupportsArg(helpText, "--fit"))
        {
            if (fit.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("--fit");
                args.Add("off");
            }
            else if (fit.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("--fit");
                args.Add("on");
            }

            return;
        }

        if (fit.Equals("Disabled", StringComparison.OrdinalIgnoreCase) && SupportsArg(helpText, "--no-fit"))
        {
            args.Add("--no-fit");
        }
    }

    private static bool SupportsArg(string helpText, string arg)
        => !string.IsNullOrWhiteSpace(helpText)
            && helpText.Contains(arg, StringComparison.OrdinalIgnoreCase);
}
