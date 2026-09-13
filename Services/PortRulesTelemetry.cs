using System.Globalization;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Live Port-rule counts for the current local turn. Not a stop.
/// Off rules show no tally.
/// </summary>
public sealed record PortRulesTelemetry
{
    public static PortRulesTelemetry Empty { get; } = new();

    public int Omitted { get; init; }
    public int ObserveOnly { get; init; }
    public int RapidStreak { get; init; }
    public int PicturesKept { get; init; }
    public int QuietSeconds { get; init; }
    public int LoadingRetries { get; init; }
    public int DumpCount { get; init; }
    public bool RepeatedCommandThisTurn { get; init; }
    public bool HasLocalTurn { get; init; }

    public static string Fraction(int current, int limit)
        => current.ToString(CultureInfo.InvariantCulture)
           + " / "
           + limit.ToString(CultureInfo.InvariantCulture);

    public string MillAtOmittedLine(PortForwardingRules rules)
    {
        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        if (!live.Enabled || !live.MillAtOmittedEnabled || !HasLocalTurn)
        {
            return string.Empty;
        }

        return Fraction(Omitted, live.RunawayOmittedResults) + " this turn";
    }

    public string ClosedLoopCeilingLine(PortForwardingRules rules)
    {
        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        if (!live.Enabled || !live.ClosedLoopCeilingEnabled || !HasLocalTurn)
        {
            return string.Empty;
        }

        return Fraction(Omitted, live.ClosedLoopRunawayOmittedResults) + " this turn";
    }

    public string ObserveOnlyLine(PortForwardingRules rules)
    {
        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        if (!live.Enabled || !live.ObserveOnlyMillEnabled || !HasLocalTurn)
        {
            return string.Empty;
        }

        return Fraction(ObserveOnly, live.ObserveOnlyMillCount);
    }

    public string RapidChurnLine(PortForwardingRules rules)
    {
        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        if (!live.Enabled || !live.RapidChurnEnabled || !HasLocalTurn)
        {
            return string.Empty;
        }

        return Fraction(RapidStreak, live.RapidChurnConsecutive);
    }

    public string MaxPicturesLine(PortForwardingRules rules)
    {
        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        if (!live.Enabled || !live.MaxPicturesEnabled || !HasLocalTurn)
        {
            return string.Empty;
        }

        return Fraction(PicturesKept, live.MaxForwardedImages);
    }

    public string HangLine(PortForwardingRules rules)
    {
        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        if (!live.Enabled || !live.HangEnabled || QuietSeconds < 0)
        {
            return string.Empty;
        }

        if (!HasLocalTurn && QuietSeconds <= 0)
        {
            return string.Empty;
        }

        return "quiet "
               + QuietSeconds.ToString(CultureInfo.InvariantCulture)
               + " / "
               + live.FirstByteSeconds.ToString(CultureInfo.InvariantCulture)
               + " s";
    }

    public string Loading503Line(PortForwardingRules rules)
    {
        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        if (!live.Enabled || !live.Loading503Enabled || !HasLocalTurn)
        {
            return string.Empty;
        }

        return Fraction(LoadingRetries, live.LoadingRetryCount);
    }

    public string DumpLine(PortForwardingRules rules)
    {
        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        if (!live.Enabled || !live.DiagnosticDumpEnabled || !HasLocalTurn)
        {
            return string.Empty;
        }

        return Fraction(DumpCount, LocalSessionArtifactPolicy.HaltThreshold);
    }

    public string RepeatedCommandLine(PortForwardingRules rules)
    {
        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        if (!live.Enabled || !live.RepeatedCommandEnabled || !HasLocalTurn)
        {
            return string.Empty;
        }

        return RepeatedCommandThisTurn ? "same command this turn" : "—";
    }

    public string CompactDiagnosticLine(PortForwardingRules rules)
    {
        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        if (!live.Enabled || !HasLocalTurn)
        {
            return string.Empty;
        }

        return "mill "
               + (live.MillAtOmittedEnabled ? Fraction(Omitted, live.RunawayOmittedResults) : "off")
               + " observe "
               + (live.ObserveOnlyMillEnabled ? Fraction(ObserveOnly, live.ObserveOnlyMillCount) : "off")
               + " rapid "
               + (live.RapidChurnEnabled ? Fraction(RapidStreak, live.RapidChurnConsecutive) : "off")
               + " pictures "
               + (live.MaxPicturesEnabled ? Fraction(PicturesKept, live.MaxForwardedImages) : "off");
    }
}
