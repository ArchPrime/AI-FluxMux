using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// A single measured prefill sample: the prompt token count and the measured
/// request-sent → first-byte duration (which includes the prefill plus the first-token
/// generation). These are the raw observations the performance model fits against.
/// </summary>
public sealed record PrefillSample(int PromptTokens, long FirstByteMs);

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

    private readonly List<PrefillSample> _samples = new();
    private double _tokensPerSec = DefaultTokensPerSec;
    private double _overheadMs = 0.0;
    private bool _hasData;
    private double _lastMeasuredSpeed = 0.0;

    public PrefillPerformanceModel(string path)
    {
        _path = path;
        Load();
    }

    /// <summary>
    /// True when the model has been fitted from at least one real sample.
    /// </summary>
    public bool HasData
    {
        get { lock (_lock) { return _hasData; } }
    }

    /// <summary>
    /// The current fitted prefill speed (tokens/sec), or the conservative default when no
    /// data exists yet. This is the speed the estimate is based on, suitable for display.
    /// </summary>
    public double CurrentSpeedTokensPerSec
    {
        get { lock (_lock) { return _hasData ? _tokensPerSec : DefaultTokensPerSec; } }
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
    /// Estimates the prefill duration in seconds for a given prompt token count. Returns 0
    /// when the token count is unknown (<= 0).
    /// </summary>
    public int EstimateSeconds(int promptTokens)
    {
        if (promptTokens <= 0)
        {
            return 0;
        }

        double speed;
        double overheadMs;
        lock (_lock)
        {
            speed = _hasData ? _tokensPerSec : DefaultTokensPerSec;
            overheadMs = _hasData ? _overheadMs : 0.0;
        }

        if (speed <= 0)
        {
            speed = DefaultTokensPerSec;
        }

        var estimatedMs = overheadMs + promptTokens / speed * 1000.0;
        return (int)Math.Ceiling(estimatedMs / 1000.0);
    }

    /// <summary>
    /// Records a measured prefill sample, refits the model, and persists it. Samples with a
    /// prompt below <see cref="MinFitTokens"/> are ignored for fitting (first-token time
    /// dominates there) but the most recent one still updates the "last measured" speed.
    /// </summary>
    public void RecordSample(int promptTokens, long firstByteMs)
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
                // The first-byte time for a small prompt is dominated by the first-token
                // generation time, not the prefill time, so it's not representative of the
                // model's true cold-prefill speed. But it's still useful for display.
                _lastMeasuredSpeed = promptTokens / (firstByteMs / 1000.0);
                return;
            }

            // Only record a sample for fitting when the prefill is a FULL prefill (not a warm-cache
            // DELTA prefill). A delta prefill completes in well under a second, so its implied speed
            // (fullPromptTokens / deltaSeconds) is absurdly high and not representative of the
            // model's true cold-prefill speed. A full prefill takes many seconds to minutes. Using
            // the first-byte duration as the discriminator: a delta prefill is sub-second, while a
            // full prefill is well above MinFullPrefillMs (5s). This is the SOLE mechanism for
            // distinguishing full prefills from delta prefills — there is intentionally NO speed cap,
            // because capping the speed would mask legitimate fast full prefills (e.g. on next-gen
            // hardware) and hide real diagnostic anomalies.
            if (firstByteMs < MinFullPrefillMs)
            {
                return; // delta prefill: not representative of the cold-prefill speed
            }

            // Track the most recent measured speed for display. Only update it for FULL prefills
            // (not delta prefills), so the displayed speed is always representative of the model's
            // true cold-prefill speed. No cap is applied: the speed is recorded at its actual value.
            _lastMeasuredSpeed = promptTokens / (firstByteMs / 1000.0);

            _samples.Add(new PrefillSample(promptTokens, firstByteMs));
            while (_samples.Count > MaxSamples)
            {
                _samples.RemoveAt(0);
            }

            Refit();
            Save();
        }
    }

    /// <summary>
    /// Fits <c>prefillMs = overheadMs + tokens / tokensPerSec</c> to the retained samples via
    /// least squares. We fit in the linear form <c>ms = a + b * tokens</c> where
    /// <c>b = 1000 / tokensPerSec</c> (ms per token) and <c>a = overheadMs</c>.
    ///
    /// Outlier rejection: a cold-load turn (model still loading from disk) or a one-off stall
    /// produces a first-byte duration that is an order of magnitude larger than the median. Such
    /// a sample would otherwise dominate the least-squares fit and drive the speed to the clamp
    /// floor. We reject any sample whose implied speed (tokens / firstByteMs) is more than
    /// <see cref="OutlierSpeedRatio"/> away from the median implied speed before fitting.
    /// </summary>
    private void Refit()
    {
        var usable = _samples
            .Where(s => s.FirstByteMs > 0 && s.PromptTokens > 0)
            .ToList();
        if (usable.Count < 2)
        {
            // Not enough data to fit a slope; fall back to a single-point speed estimate.
            if (usable.Count == 1)
            {
                var s = usable[0];
                _tokensPerSec = s.PromptTokens / (s.FirstByteMs / 1000.0);
                _overheadMs = 0.0;
                _hasData = true;
            }
            return;
        }

        // Reject outliers: compute the median implied speed (tokens/sec) and drop any sample
        // whose implied speed is more than OutlierSpeedRatio away from the median. This removes
        // cold-load turns and one-off stalls without discarding normal variance.
        var speeds = usable
            .Select(s => s.PromptTokens / (s.FirstByteMs / 1000.0))
            .OrderBy(v => v)
            .ToList();
        var medianSpeed = speeds.Count % 2 == 1
            ? speeds[speeds.Count / 2]
            : (speeds[speeds.Count / 2 - 1] + speeds[speeds.Count / 2]) / 2.0;
        usable = usable
            .Where(s =>
            {
                var speed = s.PromptTokens / (s.FirstByteMs / 1000.0);
                return medianSpeed > 0 && speed >= medianSpeed / OutlierSpeedRatio && speed <= medianSpeed * OutlierSpeedRatio;
            })
            .ToList();

        // If outlier rejection left fewer than 2 samples, fall back to the median speed directly
        // (a single robust point) rather than fitting a degenerate slope.
        if (usable.Count < 2)
        {
            _tokensPerSec = medianSpeed;
            _overheadMs = 0.0;
            _hasData = true;
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
            _tokensPerSec = 1000.0 / b;
        }
        else
        {
            // Degenerate fit (no positive slope); use the aggregate speed instead.
            _tokensPerSec = usable.Sum(s => s.PromptTokens) / (usable.Sum(s => s.FirstByteMs) / 1000.0);
        }

        // Clamp the overhead to a sane range: it should be small and non-negative. A large
        // negative intercept is a fitting artifact; a huge positive one is not physical.
        _overheadMs = Math.Clamp(a, 0.0, 5000.0);

        // Guard against a degenerate fit with a lower bound only. There is intentionally NO upper
        // bound: the fitted speed is recorded at its actual value, so a legitimate fast full prefill
        // (e.g. on next-gen hardware) is not masked. The MinFullPrefillMs duration threshold (which
        // rejects delta prefills) is the sole mechanism for ensuring the fitted speed is
        // representative of a full prefill, not a warm-cache delta.
        _tokensPerSec = Math.Max(_tokensPerSec, 50.0);
        _hasData = true;
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

            if (root.TryGetProperty("tokensPerSec", out var vSpeed) && vSpeed.TryGetDouble(out var speed))
            {
                // Load the persisted speed at its actual value. No cap is applied: the
                // MinFullPrefillMs duration threshold (which filters out delta-prefill samples
                // below) ensures the re-fit is based on full prefills only, so the loaded value
                // is representative of a full prefill. A polluted value from an older build
                // (which may have recorded a delta prefill) is corrected by the re-fit below.
                _tokensPerSec = speed;
            }
            if (root.TryGetProperty("overheadMs", out var vOverhead) && vOverhead.TryGetDouble(out var overhead))
            {
                _overheadMs = overhead;
            }
            if (root.TryGetProperty("hasData", out var vHasData))
            {
                _hasData = vHasData.GetBoolean();
            }
            if (root.TryGetProperty("lastMeasuredSpeed", out var vLast) && vLast.TryGetDouble(out var last))
            {
                // Load the persisted last-measured speed at its actual value (no cap). The
                // MinFullPrefillMs filter below ensures only full-prefill samples are retained,
                // so the re-fit (if any) is based on full prefills only.
                _lastMeasuredSpeed = last;
            }
            if (root.TryGetProperty("samples", out var vSamples) && vSamples.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in vSamples.EnumerateArray())
                {
                    if (item.TryGetProperty("promptTokens", out var vTokens) && item.TryGetProperty("firstByteMs", out var vMs))
                    {
                        var tokens = vTokens.GetInt32();
                        var ms = vMs.GetInt64();
                        // Drop delta-prefill samples (first-byte duration below MinFullPrefillMs)
                        // so they cannot pollute the re-fit on load. A delta prefill's implied speed
                        // (fullPromptTokens / deltaSeconds) is absurdly high and not representative
                        // of the model's true cold-prefill speed.
                        if (ms < MinFullPrefillMs)
                        {
                            continue;
                        }
                        _samples.Add(new PrefillSample(tokens, ms));
                    }
                }
                while (_samples.Count > MaxSamples)
                {
                    _samples.RemoveAt(0);
                }
            }

            // Re-fit from the loaded samples so the persisted constants are always consistent
            // with the current outlier-rejection logic. This corrects any stale/bad fit that was
            // saved by an older build (e.g. one skewed by a cold-load outlier).
            if (_samples.Count > 0)
            {
                Refit();
            }
            else
            {
                // All loaded samples were delta prefills (filtered out), so there is no reliable
                // full-prefill data. Reset to the default speed so the model learns from new
                // full-prefill samples instead of using a polluted/stale value.
                _tokensPerSec = DefaultTokensPerSec;
                _overheadMs = 0.0;
                _hasData = false;
            }
        }
        catch
        {
            // Best-effort: a corrupt file just means we start from the conservative default.
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
                tokensPerSec = _tokensPerSec,
                overheadMs = _overheadMs,
                hasData = _hasData,
                lastMeasuredSpeed = _lastMeasuredSpeed,
                samples = _samples.Select(s => new { promptTokens = s.PromptTokens, firstByteMs = s.FirstByteMs })
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
