using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// A single measured prefill sample: the prompt token count, the measured
/// request-sent → first-byte duration, and whether this was a cold prefill
/// (model just loaded, includes model loading time) or a warm prefill
/// (model already loaded, pure prefill time).
/// </summary>
public sealed record PrefillSample(int PromptTokens, long FirstByteMs, bool IsCold);

/// <summary>
/// A self-refining prefill performance model for a specific model/variant.
///
/// Prefill time is roughly linear in the prompt token count, with a small fixed overhead
/// (request setup + first-token generation). We model it as:
///
///     prefillMs(tokens) = overheadMs + tokens / tokensPerSec
///
/// The two constants (<c>tokensPerSec</c> and <c>overheadMs</c>) are refined from real data:
///   1. Online chat-turn samples (the primary source) — each local turn records
///      (promptTokens, measuredFirstByteMs) and the model refits. This converges to the
///      actual hardware speed within a few turns.
///   2. A conservative default when no data exists yet, so the first estimate is biased
///      pessimistic (the progress bar over-estimates rather than dying early).
///
/// The model is persisted per model/variant so the refined constants survive restarts.
/// </summary>
public sealed class PrefillPerformanceModel
{
    /// <summary>
    /// Conservative cold-start prefill speed (tokens/sec) used when no measured data exists.
    /// Deliberately pessimistic so the progress bar over-estimates on a true cold start;
    /// subsequent turns refine the real speed.
    /// </summary>
    public const double DefaultTokensPerSec = 500.0;

    /// <summary>
    /// Minimum prompt token count for a sample to be trusted for fitting. Below this, the
    /// first-token generation time dominates the measured first-byte duration and would
    /// pollute the prefill slope.
    /// </summary>
    public const int MinFitTokens = 512;

    /// <summary>
    /// Maximum number of rolling samples retained. Older samples are dropped so the fit
    /// tracks recent hardware behavior (thermal throttling, background load, etc.).
    /// </summary>
    public const int MaxSamples = 20;

    /// <summary>
    /// Outlier rejection ratio: a sample whose implied prefill speed is more than this factor
    /// away from the median implied speed is rejected before fitting. This removes cold-load
    /// turns (model still loading from disk) and one-off stalls, which would otherwise dominate
    /// the least-squares fit. A factor of 3 keeps normal variance while dropping order-of-
    /// magnitude outliers.
    /// </summary>
    public const double OutlierSpeedRatio = 3.0;

    /// <summary>
    /// Minimum first-byte duration (ms) for a sample to be considered a FULL prefill (as opposed
    /// to a warm-cache DELTA prefill). A delta prefill (KV cache already resident, only the new
    /// tokens processed) completes in well under a second, so its implied speed
    /// (fullPromptTokens / deltaSeconds) is absurdly high and not representative of the model's
    /// true cold-prefill speed. A full prefill (processing the entire prompt from scratch) takes
    /// many seconds to minutes. Only samples with a first-byte duration above this threshold are
    /// recorded for fitting, so the learned speed reflects full prefills, not delta prefills.
    /// 5000 ms (5s) is well above any realistic delta prefill and well below any realistic full
    /// prefill for a prompt over MinFitTokens.
    ///
    /// This duration threshold is the SOLE mechanism for distinguishing full prefills from delta
    /// prefills. There is intentionally NO speed cap: capping the speed would mask legitimate fast
    /// full prefills (e.g. on next-gen hardware) and hide real diagnostic anomalies. The duration
    /// threshold is accurate and future-proof because it's based on the *time* (which is
    /// hardware-independent for a full prefill), not the *speed* (which varies with hardware).
    /// </summary>
    public const long MinFullPrefillMs = 5000;

    private readonly string _path;
    private readonly object _lock = new();

    // Cold-prefill model: first turn after launch (includes model loading from disk).
    private readonly List<PrefillSample> _coldSamples = new();
    private double _coldTokensPerSec = DefaultTokensPerSec;
    private double _coldOverheadMs = 0.0;
    private bool _coldHasData;

    // Warm-prefill model: subsequent turns where the KV cache was evicted but the model
    // is already loaded (pure prefill speed, much faster than cold).
    private readonly List<PrefillSample> _warmSamples = new();
    private double _warmTokensPerSec = DefaultTokensPerSec;
    private double _warmOverheadMs = 0.0;
    private bool _warmHasData;

    // RAM-spill model: prefills where the prompt exceeds the VRAM-resident context and the
    // overflow runs in ordinary RAM. This is a different regime than VRAM-resident prefills
    // (slower, dominated by RAM bandwidth), so it gets its own fitted speed. The user needs
    // an accurate estimate of how long the RAM load will take.
    private readonly List<PrefillSample> _ramSpillSamples = new();
    private double _ramSpillTokensPerSec = DefaultTokensPerSec;
    private double _ramSpillOverheadMs = 0.0;
    private bool _ramSpillHasData;

    private double _lastMeasuredSpeed = 0.0;

    public PrefillPerformanceModel(string path)
    {
        _path = path;
        Load();
    }

    /// <summary>
    /// True when the model has been fitted from at least one real sample (cold or warm).
    /// </summary>
    public bool HasData
    {
        get { lock (_lock) { return _coldHasData || _warmHasData; } }
    }

    /// <summary>
    /// The fitted prefill speed (tokens/sec) for COLD prefills (first turn after launch).
    /// </summary>
    public double ColdSpeedTokensPerSec
    {
        get { lock (_lock) { return _coldHasData ? _coldTokensPerSec : DefaultTokensPerSec; } }
    }

    /// <summary>
    /// The fitted prefill speed (tokens/sec) for WARM prefills. Falls back to the cold
    /// speed when no warm data exists yet.
    /// </summary>
    public double WarmSpeedTokensPerSec
    {
        get { lock (_lock) { return _warmHasData ? _warmTokensPerSec : (_coldHasData ? _coldTokensPerSec : DefaultTokensPerSec); } }
    }

    /// <summary>
    /// The current fitted prefill speed for display. Prefers the WARM speed: in the normal
    /// workflow the operator resumes a chat on an already-loaded model, so the prefill is warm,
    /// and the warm model is the one that actually accumulates data (the cold model is fit from
    /// at most one sample — the first turn after a launch — and is then frozen, because the
    /// operator is usually forced to switch models before a second cold prefill on the same
    /// variant ever happens). A degenerate cold value (a cold-load turn where the model was
    /// still loading from disk → huge firstByteMs → implied speed ≈ 0) would otherwise make the
    /// prefill bar show "0 tok/s" for the entire prefill and never resolve, because it's a
    /// learned constant, not a live measurement, and the cold model never re-fits. Fall back to
    /// the cold speed, then to the default, when warm has no data.
    /// </summary>
    public double CurrentSpeedTokensPerSec
    {
        get
        {
            lock (_lock)
            {
                var warm = _warmHasData ? _warmTokensPerSec : 0.0;
                if (warm > 0)
                {
                    return warm;
                }

                var cold = _coldHasData ? _coldTokensPerSec : 0.0;
                if (cold > 0)
                {
                    return cold;
                }

                return DefaultTokensPerSec;
            }
        }
    }

    /// <summary>
    /// The measured prefill speed (tokens/sec) from the most recent recorded sample, or 0
    /// when no sample has been recorded yet.
    /// </summary>
    public double LastMeasuredSpeedTokensPerSec
    {
        get { lock (_lock) { return _lastMeasuredSpeed; } }
    }

    /// <summary>
    /// Estimates the prefill duration in seconds for a given prompt token count. Uses the
    /// COLD model when <paramref name="isCold"/> is true (first turn after launch), and the
    /// WARM model when false (subsequent turns where the KV cache may have been evicted).
    /// Returns 0 when the token count is unknown (<= 0).
    /// </summary>
    public int EstimateSeconds(int promptTokens, bool isCold)
    {
        if (promptTokens <= 0)
        {
            return 0;
        }

        double speed;
        double overheadMs;
        lock (_lock)
        {
            if (isCold)
            {
                speed = _coldHasData ? _coldTokensPerSec : DefaultTokensPerSec;
                overheadMs = _coldHasData ? _coldOverheadMs : 0.0;
            }
            else
            {
                if (_warmHasData)
                {
                    speed = _warmTokensPerSec;
                    overheadMs = _warmOverheadMs;
                }
                else
                {
                    speed = _coldHasData ? _coldTokensPerSec : DefaultTokensPerSec;
                    overheadMs = _coldHasData ? _coldOverheadMs : 0.0;
                }
            }
        }

        if (speed <= 0)
        {
            speed = DefaultTokensPerSec;
        }

        var estimatedMs = overheadMs + promptTokens / speed * 1000.0;
        return (int)Math.Ceiling(estimatedMs / 1000.0);
    }

    /// <summary>
    /// Records a measured prefill sample, refits the appropriate model (cold or warm), and
    /// persists it. Samples with a prompt below <see cref="MinFitTokens"/> are ignored for
    /// fitting (first-token time dominates there) but the most recent one still updates the
    /// "last measured" speed. Delta prefills (first-byte below <see cref="MinFullPrefillMs"/>)
    /// are not recorded for fitting.
    /// </summary>
    public void RecordSample(int promptTokens, long firstByteMs, bool isCold)
    {
        if (promptTokens <= 0 || firstByteMs <= 0)
        {
            return;
        }

        lock (_lock)
        {
            if (promptTokens < MinFitTokens)
            {
                // Small prompt: update the last-measured speed for display, but don't fit.
                _lastMeasuredSpeed = promptTokens / (firstByteMs / 1000.0);
                return;
            }

            // Only record a sample for fitting when the prefill is a FULL prefill (not a
            // warm-cache DELTA prefill). A delta prefill completes in well under a second, so
            // its implied speed is absurdly high and not representative of the full-prefill
            // speed. A full prefill takes many seconds to minutes.
            if (firstByteMs < MinFullPrefillMs)
            {
                return; // delta prefill: not representative of the full-prefill speed
            }

            // Track the most recent measured speed for display.
            _lastMeasuredSpeed = promptTokens / (firstByteMs / 1000.0);

            var samples = isCold ? _coldSamples : _warmSamples;
            samples.Add(new PrefillSample(promptTokens, firstByteMs, isCold));
            while (samples.Count > MaxSamples)
            {
                samples.RemoveAt(0);
            }

            if (isCold)
            {
                RefitInto(_coldSamples, ref _coldTokensPerSec, ref _coldOverheadMs, ref _coldHasData);
            }
            else
            {
                RefitInto(_warmSamples, ref _warmTokensPerSec, ref _warmOverheadMs, ref _warmHasData);
            }

            Save();
        }
    }

    /// <summary>
    /// Records a measured RAM-spill prefill sample (prompt exceeds the VRAM-resident context,
    /// overflow runs in ordinary RAM). This is a different regime than VRAM-resident prefills,
    /// so it gets its own fitted speed. The user needs an accurate estimate of how long the
    /// RAM load will take.
    /// </summary>
    public void RecordRamSpillSample(int promptTokens, long firstByteMs)
    {
        if (promptTokens <= 0 || firstByteMs <= 0)
        {
            return;
        }

        lock (_lock)
        {
            if (promptTokens < MinFitTokens)
            {
                _lastMeasuredSpeed = promptTokens / (firstByteMs / 1000.0);
                return;
            }

            if (firstByteMs < MinFullPrefillMs)
            {
                return; // delta prefill: not representative
            }

            _lastMeasuredSpeed = promptTokens / (firstByteMs / 1000.0);

            _ramSpillSamples.Add(new PrefillSample(promptTokens, firstByteMs, IsCold: false));
            while (_ramSpillSamples.Count > MaxSamples)
            {
                _ramSpillSamples.RemoveAt(0);
            }

            RefitInto(_ramSpillSamples, ref _ramSpillTokensPerSec, ref _ramSpillOverheadMs, ref _ramSpillHasData);
            Save();
        }
    }

    /// <summary>
    /// Estimates the prefill time (seconds) for a RAM-spill prefill (prompt exceeds the
    /// VRAM-resident context). Uses the dedicated RAM-spill model if it has data, otherwise
    /// falls back to the cold/warm VRAM model (which is better than nothing).
    /// </summary>
    public int RamSpillEstimateSeconds(int promptTokens)
    {
        if (promptTokens <= 0)
        {
            return 0;
        }

        lock (_lock)
        {
            if (_ramSpillHasData)
            {
                double seconds = _ramSpillOverheadMs / 1000.0 + promptTokens / _ramSpillTokensPerSec;
                return (int)Math.Ceiling(seconds);
            }

            // No RAM-spill data yet: fall back to the cold/warm VRAM estimate. This is
            // pessimistic (VRAM is faster than RAM) but better than a hardcoded guess.
            return EstimateSeconds(promptTokens, isCold: true);
        }
    }
    /// least squares. We fit in the linear form <c>ms = a + b * tokens</c> where
    /// <c>b = 1000 / tokensPerSec</c> (ms per token) and <c>a = overheadMs</c>.
    ///
    /// Outlier rejection: a cold-load turn (model still loading from disk) or a one-off stall
    /// produces a first-byte duration that is an order of magnitude larger than the median. Such
    /// a sample would otherwise dominate the least-squares fit and drive the speed to the clamp
    /// floor. We reject any sample whose implied speed (tokens / firstByteMs) is more than
    /// <see cref="OutlierSpeedRatio"/> away from the median implied speed before fitting.
    /// </summary>
    private void RefitInto(List<PrefillSample> samples, ref double tokensPerSec, ref double overheadMs, ref bool hasData)
    {
        var usable = samples
            .Where(s => s.FirstByteMs > 0 && s.PromptTokens > 0)
            .ToList();
        if (usable.Count < 2)
        {
            // Not enough data to fit a slope; fall back to a single-point speed estimate.
            if (usable.Count == 1)
            {
                var s = usable[0];
                // Apply the same lower bound as the multi-sample fit path. A single cold-load
                // turn (model still loading from disk) can have a huge firstByteMs, making the
                // implied speed near 0. Persisting that would make the prefill bar show
                // "0 tok/s" forever (it's a learned constant, not a live measurement).
                tokensPerSec = Math.Max(s.PromptTokens / (s.FirstByteMs / 1000.0), 50.0);
                overheadMs = 0.0;
                hasData = true;
            }
            else
            {
                tokensPerSec = DefaultTokensPerSec;
                overheadMs = 0.0;
                hasData = false;
            }
            return;
        }

        // Reject outliers: compute the 75th-percentile implied speed (tokens/sec) and drop any
        // sample whose implied speed is more than OutlierSpeedRatio below that percentile.
        // Prefill speed is bounded above by hardware but unbounded below by stalls/cold-loads,
        // so we anchor on the upper side of the distribution: the fast samples are the true
        // warm speed, and the slow ones are cold-loads, RAM-spill events, or one-off stalls.
        // Using the median (as before) dragged the estimate down because a few slow samples
        // pulled the median into the slow range, causing the fast samples to be rejected as
        // "outliers" and the estimate to converge on the slow speed.
        var speeds = usable
            .Select(s => s.PromptTokens / (s.FirstByteMs / 1000.0))
            .OrderBy(v => v)
            .ToList();
        // 75th percentile: the speed at which 75% of samples are slower. This anchors on the
        // fast side without being as aggressive as the max (which could be a one-off spike).
        int p75Index = (int)Math.Ceiling(speeds.Count * 0.75) - 1;
        p75Index = Math.Clamp(p75Index, 0, speeds.Count - 1);
        var referenceSpeed = speeds[p75Index];
        usable = usable
            .Where(s =>
            {
                var speed = s.PromptTokens / (s.FirstByteMs / 1000.0);
                // Keep samples within OutlierSpeedRatio of the 75th percentile. This removes
                // very slow samples (cold-loads, stalls) while keeping the fast warm samples.
                return referenceSpeed > 0 && speed >= referenceSpeed / OutlierSpeedRatio;
            })
            .ToList();

        // If outlier rejection left fewer than 2 samples, fall back to the 75th-percentile speed
        // directly (a single robust point) rather than fitting a degenerate slope.
        if (usable.Count < 2)
        {
            tokensPerSec = referenceSpeed;
            overheadMs = 0.0;
            hasData = true;
            return;
        }

        // Least-squares fit of ms = a + b*tokens.
        double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;
        int n = usable.Count;
        foreach (var s in usable)
        {
            double x = s.PromptTokens;
            double y = s.FirstByteMs;
            sumX += x;
            sumY += y;
            sumXX += x * x;
            sumXY += x * y;
        }

        double denom = n * sumXX - sumX * sumX;
        double b = denom != 0 ? (n * sumXY - sumX * sumY) / denom : 0.0;
        double a = b != 0 ? (sumY - b * sumX) / n : sumY / n;

        // b is ms per token; tokensPerSec = 1000 / b. Guard against non-positive b.
        if (b > 0)
        {
            tokensPerSec = 1000.0 / b;
        }
        else
        {
            // Degenerate fit (no positive slope); use the aggregate speed instead.
            tokensPerSec = usable.Sum(s => s.PromptTokens) / (usable.Sum(s => s.FirstByteMs) / 1000.0);
        }

        // Clamp the overhead to a sane range: it should be small and non-negative. A large
        // negative intercept is a fitting artifact; a huge positive one is not physical.
        overheadMs = Math.Clamp(a, 0.0, 5000.0);

        // Guard against a degenerate fit with a lower bound only. There is intentionally NO upper
        // bound: the fitted speed is recorded at its actual value, so a legitimate fast full prefill
        // (e.g. on next-gen hardware) is not masked.
        tokensPerSec = Math.Max(tokensPerSec, 50.0);
        hasData = true;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var json = File.ReadAllText(_path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            LoadSamples(root, "coldSamples", _coldSamples, isCold: true);
            LoadSamples(root, "warmSamples", _warmSamples, isCold: false);
            LoadSamples(root, "ramSpillSamples", _ramSpillSamples, isCold: false);

            // Backward compatibility: the old format had a single "samples" array that mixed
            // cold and warm prefills. We can no longer distinguish them, so we do NOT load the
            // old samples — loading them all as cold would skew the cold fit with the fast warm
            // samples. The model re-learns cleanly within a few turns with proper cold/warm
            // tagging. (The old fitted constants are intentionally ignored for the same reason.)

            // Re-fit from the loaded samples so the persisted constants are always consistent
            // with the current outlier-rejection logic.
            if (_coldSamples.Count > 0)
            {
                RefitInto(_coldSamples, ref _coldTokensPerSec, ref _coldOverheadMs, ref _coldHasData);
            }
            if (_warmSamples.Count > 0)
            {
                RefitInto(_warmSamples, ref _warmTokensPerSec, ref _warmOverheadMs, ref _warmHasData);
            }
            if (_ramSpillSamples.Count > 0)
            {
                RefitInto(_ramSpillSamples, ref _ramSpillTokensPerSec, ref _ramSpillOverheadMs, ref _ramSpillHasData);
            }

            if (root.TryGetProperty("lastMeasuredSpeed", out var vLast) && vLast.TryGetDouble(out var last))
            {
                _lastMeasuredSpeed = last;
            }
        }
        catch
        {
            // Best-effort: a corrupt file just means we start from the conservative default.
        }
    }

    private static void LoadSamples(JsonElement root, string propertyName, List<PrefillSample> target, bool isCold)
    {
        if (!root.TryGetProperty(propertyName, out var vSamples) || vSamples.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in vSamples.EnumerateArray())
        {
            if (item.TryGetProperty("promptTokens", out var vTokens) && item.TryGetProperty("firstByteMs", out var vMs))
            {
                var tokens = vTokens.GetInt32();
                var ms = vMs.GetInt64();
                // Drop delta-prefill samples (first-byte duration below MinFullPrefillMs)
                // so they cannot pollute the re-fit on load.
                if (ms < MinFullPrefillMs)
                {
                    continue;
                }
                target.Add(new PrefillSample(tokens, ms, isCold));
            }
        }
        while (target.Count > MaxSamples)
        {
            target.RemoveAt(0);
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var value = new
            {
                coldTokensPerSec = _coldTokensPerSec,
                coldOverheadMs = _coldOverheadMs,
                coldHasData = _coldHasData,
                warmTokensPerSec = _warmTokensPerSec,
                warmOverheadMs = _warmOverheadMs,
                warmHasData = _warmHasData,
                ramSpillTokensPerSec = _ramSpillTokensPerSec,
                ramSpillOverheadMs = _ramSpillOverheadMs,
                ramSpillHasData = _ramSpillHasData,
                lastMeasuredSpeed = _lastMeasuredSpeed,
                coldSamples = _coldSamples.Select(s => new { promptTokens = s.PromptTokens, firstByteMs = s.FirstByteMs }),
                warmSamples = _warmSamples.Select(s => new { promptTokens = s.PromptTokens, firstByteMs = s.FirstByteMs }),
                ramSpillSamples = _ramSpillSamples.Select(s => new { promptTokens = s.PromptTokens, firstByteMs = s.FirstByteMs })
            };
            var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch
        {
            // Best-effort persistence; a failure just means the next start re-learns.
        }
    }

    /// <summary>
    /// Builds a safe, filesystem-friendly key from a model/variant pair, mirroring the
    /// convention used by the stress-test history files.
    /// </summary>
    public static string BuildKey(string model, string variant)
        => Regex.Replace(model + "|" + variant, "[^\\w.-]", "_").Trim('_');

    /// <summary>
    /// Formats a speed for display, e.g. "750 tok/s". Returns an empty string when the speed
    /// is not positive.
    /// </summary>
    public static string FormatSpeed(double tokensPerSec)
        => tokensPerSec > 0
            ? ((int)Math.Round(tokensPerSec)).ToString(CultureInfo.InvariantCulture) + " tok/s"
            : string.Empty;
}
