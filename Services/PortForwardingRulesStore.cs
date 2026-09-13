using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Allow-listed Port rules JSON beside fluxmux_config.json. Nothing is
/// required next to the exe. Dirty files are quarantined; defaults stay in code.
/// </summary>
public sealed class PortForwardingRulesStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _path;

    public PortForwardingRulesStore(string path)
    {
        _path = path;
    }

    public string Path => _path;

    public string LastLoadNotice { get; private set; } = string.Empty;

    public static string ResolvePath(string configPath)
        => FluxMuxConfigPaths.ResolveSibling(configPath, FluxMuxConfigPaths.PortRulesFileName);

    public static PortForwardingRulesStore BesideConfig(string configPath)
        => new(ResolvePath(configPath));

    public PortForwardingRules Load()
    {
        var root = JsonFileQuarantine.ReadObjectOrEmpty(_path, out var notice);
        LastLoadNotice = notice;
        return Read(root).Clamp();
    }

    public void Save(PortForwardingRules rules)
    {
        var clamped = (rules ?? PortForwardingRules.Defaults).Clamp();
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(_path, Write(clamped).ToJsonString(WriteOptions));
    }

    public static PortForwardingRules Read(JsonObject root)
    {
        if (root is null || root.Count == 0)
        {
            return PortForwardingRules.Defaults;
        }

        var defaults = PortForwardingRules.Defaults;
        var rules = new PortForwardingRules
        {
            Enabled = ReadBool(root, "enabled", defaults.Enabled),
            CompactEnabled = ReadBool(root, "compactEnabled", defaults.CompactEnabled),
            OmitEnabled = ReadBool(root, "omitEnabled", defaults.OmitEnabled),
            MaxPicturesEnabled = ReadBool(root, "maxPicturesEnabled", defaults.MaxPicturesEnabled),
            MillAtOmittedEnabled = ReadBool(root, "millAtOmittedEnabled", defaults.MillAtOmittedEnabled),
            ClosedLoopLookbackEnabled = ReadBool(root, "closedLoopLookbackEnabled", defaults.ClosedLoopLookbackEnabled),
            ClosedLoopCeilingEnabled = ReadBool(root, "closedLoopCeilingEnabled", defaults.ClosedLoopCeilingEnabled),
            ObserveOnlyMillEnabled = ReadBool(root, "observeOnlyMillEnabled", defaults.ObserveOnlyMillEnabled),
            RapidChurnEnabled = ReadBool(root, "rapidChurnEnabled", defaults.RapidChurnEnabled),
            HangEnabled = ReadBool(root, "hangEnabled", defaults.HangEnabled),
            Loading503Enabled = ReadBool(root, "loading503Enabled", defaults.Loading503Enabled),
            RepeatedCommandEnabled = ReadBool(root, "repeatedCommandEnabled", defaults.RepeatedCommandEnabled),
            DiagnosticDumpEnabled = ReadBool(root, "diagnosticDumpEnabled", defaults.DiagnosticDumpEnabled),
            CompactWatermarkPercent = ReadInt(root, "compactWatermarkPercent", defaults.CompactWatermarkPercent),
            CompactKeepTurns = ReadInt(root, "compactKeepTurns", defaults.CompactKeepTurns),
            CompactToolKeepTurns = ReadInt(root, "compactToolKeepTurns", defaults.CompactToolKeepTurns),
            CompactHeadroom = ReadInt(root, "compactHeadroom", defaults.CompactHeadroom),
            CompactPreservedUserChars = ReadInt(root, "compactPreservedUserChars", defaults.CompactPreservedUserChars),
            KeepRecentResults = ReadInt(root, "keepRecentResults", defaults.KeepRecentResults),
            PinLatestShellResults = ReadInt(root, "pinLatestShellResults", defaults.PinLatestShellResults),
            RunawayOmittedResults = ReadInt(root, "runawayOmittedResults", defaults.RunawayOmittedResults),
            ClosedLoopRunawayOmittedResults = ReadInt(root, "closedLoopRunawayOmittedResults", defaults.ClosedLoopRunawayOmittedResults),
            ObserveOnlyMillCount = ReadInt(root, "observeOnlyMillCount", defaults.ObserveOnlyMillCount),
            ClosedLoopMillLookback = ReadInt(root, "closedLoopMillLookback", defaults.ClosedLoopMillLookback),
            RapidChurnSeconds = ReadDouble(root, "rapidChurnSeconds", defaults.RapidChurnSeconds),
            RapidChurnConsecutive = ReadInt(root, "rapidChurnConsecutive", defaults.RapidChurnConsecutive),
            MinResultCharsToClear = ReadInt(root, "minResultCharsToClear", defaults.MinResultCharsToClear),
            MaxForwardedImages = ReadInt(root, "maxForwardedImages", defaults.MaxForwardedImages),
            FirstByteSeconds = ReadInt(root, "firstByteSeconds", defaults.FirstByteSeconds),
            ThinkTokensPerSecond = ReadInt(root, "thinkTokensPerSecond", defaults.ThinkTokensPerSecond),
            MaxThinkFirstByteSeconds = ReadInt(root, "maxThinkFirstByteSeconds", defaults.MaxThinkFirstByteSeconds),
            StallSeconds = ReadInt(root, "stallSeconds", defaults.StallSeconds),
            WaitLongerSeconds = ReadInt(root, "waitLongerSeconds", defaults.WaitLongerSeconds),
            DecisionSeconds = ReadInt(root, "decisionSeconds", defaults.DecisionSeconds),
            LoadingRetryCount = ReadInt(root, "loadingRetryCount", defaults.LoadingRetryCount),
            LoadingRetryDelaySeconds = ReadInt(root, "loadingRetryDelaySeconds", defaults.LoadingRetryDelaySeconds),
            SkipPrefixCacheAfterCompact = ReadBool(root, "skipPrefixCacheAfterCompact", defaults.SkipPrefixCacheAfterCompact),
            ClientMaxTokensMode = ReadString(root, "clientMaxTokensMode", defaults.ClientMaxTokensMode),
            StopHygieneMode = ReadString(root, "stopHygieneMode", defaults.StopHygieneMode)
        };
        return ApplyLegacyEnforceStops(root, rules);
    }

    private static bool HasRuleToggle(JsonObject root)
        => root.ContainsKey("compactEnabled")
           || root.ContainsKey("omitEnabled")
           || root.ContainsKey("maxPicturesEnabled")
           || root.ContainsKey("millAtOmittedEnabled")
           || root.ContainsKey("closedLoopLookbackEnabled")
           || root.ContainsKey("closedLoopCeilingEnabled")
           || root.ContainsKey("observeOnlyMillEnabled")
           || root.ContainsKey("rapidChurnEnabled")
           || root.ContainsKey("hangEnabled")
           || root.ContainsKey("loading503Enabled")
           || root.ContainsKey("repeatedCommandEnabled")
           || root.ContainsKey("diagnosticDumpEnabled");

    private static PortForwardingRules ApplyLegacyEnforceStops(JsonObject root, PortForwardingRules rules)
    {
        if (ReadBool(root, "enforceStops", true) || HasRuleToggle(root))
        {
            return rules;
        }

        return rules with
        {
            MillAtOmittedEnabled = false,
            ClosedLoopCeilingEnabled = false,
            ObserveOnlyMillEnabled = false,
            RapidChurnEnabled = false,
            HangEnabled = false,
            Loading503Enabled = false,
            RepeatedCommandEnabled = false,
            DiagnosticDumpEnabled = false
        };
    }

    public static JsonObject Write(PortForwardingRules rules)
    {
        var clamped = (rules ?? PortForwardingRules.Defaults).Clamp();
        return new JsonObject
        {
            ["enabled"] = clamped.Enabled,
            ["compactEnabled"] = clamped.CompactEnabled,
            ["omitEnabled"] = clamped.OmitEnabled,
            ["maxPicturesEnabled"] = clamped.MaxPicturesEnabled,
            ["millAtOmittedEnabled"] = clamped.MillAtOmittedEnabled,
            ["closedLoopLookbackEnabled"] = clamped.ClosedLoopLookbackEnabled,
            ["closedLoopCeilingEnabled"] = clamped.ClosedLoopCeilingEnabled,
            ["observeOnlyMillEnabled"] = clamped.ObserveOnlyMillEnabled,
            ["rapidChurnEnabled"] = clamped.RapidChurnEnabled,
            ["hangEnabled"] = clamped.HangEnabled,
            ["loading503Enabled"] = clamped.Loading503Enabled,
            ["repeatedCommandEnabled"] = clamped.RepeatedCommandEnabled,
            ["diagnosticDumpEnabled"] = clamped.DiagnosticDumpEnabled,
            ["compactWatermarkPercent"] = clamped.CompactWatermarkPercent,
            ["compactKeepTurns"] = clamped.CompactKeepTurns,
            ["compactToolKeepTurns"] = clamped.CompactToolKeepTurns,
            ["compactHeadroom"] = clamped.CompactHeadroom,
            ["compactPreservedUserChars"] = clamped.CompactPreservedUserChars,
            ["keepRecentResults"] = clamped.KeepRecentResults,
            ["pinLatestShellResults"] = clamped.PinLatestShellResults,
            ["runawayOmittedResults"] = clamped.RunawayOmittedResults,
            ["closedLoopRunawayOmittedResults"] = clamped.ClosedLoopRunawayOmittedResults,
            ["observeOnlyMillCount"] = clamped.ObserveOnlyMillCount,
            ["closedLoopMillLookback"] = clamped.ClosedLoopMillLookback,
            ["rapidChurnSeconds"] = clamped.RapidChurnSeconds,
            ["rapidChurnConsecutive"] = clamped.RapidChurnConsecutive,
            ["minResultCharsToClear"] = clamped.MinResultCharsToClear,
            ["maxForwardedImages"] = clamped.MaxForwardedImages,
            ["firstByteSeconds"] = clamped.FirstByteSeconds,
            ["thinkTokensPerSecond"] = clamped.ThinkTokensPerSecond,
            ["maxThinkFirstByteSeconds"] = clamped.MaxThinkFirstByteSeconds,
            ["stallSeconds"] = clamped.StallSeconds,
            ["waitLongerSeconds"] = clamped.WaitLongerSeconds,
            ["decisionSeconds"] = clamped.DecisionSeconds,
            ["loadingRetryCount"] = clamped.LoadingRetryCount,
            ["loadingRetryDelaySeconds"] = clamped.LoadingRetryDelaySeconds,
            ["skipPrefixCacheAfterCompact"] = clamped.SkipPrefixCacheAfterCompact,
            ["clientMaxTokensMode"] = clamped.ClientMaxTokensMode,
            ["stopHygieneMode"] = clamped.StopHygieneMode
        };
    }

    private static int ReadInt(JsonObject root, string key, int fallback)
    {
        var node = root[key];
        if (node is null)
        {
            return fallback;
        }

        try
        {
            return node.GetValue<int>();
        }
        catch
        {
            return int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
        }
    }

    private static double ReadDouble(JsonObject root, string key, double fallback)
    {
        var node = root[key];
        if (node is null)
        {
            return fallback;
        }

        try
        {
            return node.GetValue<double>();
        }
        catch
        {
            return double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
        }
    }

    private static bool ReadBool(JsonObject root, string key, bool fallback)
    {
        var node = root[key];
        if (node is null)
        {
            return fallback;
        }

        try
        {
            return node.GetValue<bool>();
        }
        catch
        {
            return bool.TryParse(node.ToString(), out var parsed)
                ? parsed
                : fallback;
        }
    }

    private static string ReadString(JsonObject root, string key, string fallback)
        => root[key]?.ToString() ?? fallback;
}
