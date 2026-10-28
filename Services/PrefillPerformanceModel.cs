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
            // Always track the most recent measured speed for display, even for small prompts.
            _lastMeasuredSpeed = promptTokens / (firstByteMs / 1000.0);

            if (promptTokens < MinFitTokens)
            {
                // Too small to fit the prefill slope reliably; don't pollute the sample set.
                return;
            }

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

        // Sanity-clamp the speed to a plausible range so a single bad sample can't produce an
        // absurd estimate. The upper bound is high (modern GPUs with a fully-loaded model can
        // prefill well past 100k tokens/sec); the lower bound guards against a degenerate fit.
        _tokensPerSec = Math.Clamp(_tokensPerSec, 50.0, 500000.0);
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
                _lastMeasuredSpeed = last;
            }
            if (root.TryGetProperty("samples", out var vSamples) && vSamples.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in vSamples.EnumerateArray())
                {
                    if (item.TryGetProperty("promptTokens", out var vTokens) && item.TryGetProperty("firstByteMs", out var vMs))
                    {
                        _samples.Add(new PrefillSample(vTokens.GetInt32(), vMs.GetInt64()));
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
