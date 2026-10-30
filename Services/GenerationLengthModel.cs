using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// A single measured generation sample: the completion token count for one finished turn.
/// </summary>
public sealed record GenerationLengthSample(int CompletionTokens);

/// <summary>
/// A self-refining model of the typical completion (generation) length for a specific
/// model/variant, learned from real turns.
///
/// The generation-phase ETA and progress bar need an estimate of how many tokens the
/// response will total. Previously this was a hard-coded 500, which is wrong for users on
/// different hardware or with different prompt styles (short answers vs. long tool-heavy
/// replies). This model records the actual completion token count of each finished turn and
/// keeps a rolling median, so the estimate converges to the user's real typical response
/// length within a few turns.
///
/// The median (rather than the mean) is used because completion lengths are right-skewed:
/// a few very long tool-calling turns would otherwise drag a mean upward. The median is
/// robust to those outliers while still tracking the typical case.
///
/// The model is persisted per model/variant so the learned length survives restarts,
/// mirroring <see cref="PrefillPerformanceModel"/>.
/// </summary>
public sealed class GenerationLengthModel
{
    /// <summary>
    /// Conservative default completion length (tokens) used when no measured data exists yet.
    /// Matches the previous hard-coded estimate so first-run behavior is unchanged.
    /// </summary>
    public const int DefaultCompletionTokens = 500;

    /// <summary>
    /// Maximum number of rolling samples retained. Older samples are dropped so the median
    /// tracks recent behavior (prompt style, tool usage) rather than a long history.
    /// </summary>
    public const int MaxSamples = 20;

    /// <summary>
    /// Minimum completion token count for a sample to be trusted. Very short replies (a few
    /// tokens) are not representative of the typical response length and would pull the
    /// median down; they are ignored for fitting.
    /// </summary>
    public const int MinFitTokens = 16;

    private readonly string _path;
    private readonly object _lock = new();

    private readonly List<int> _samples = new();
    private int _median = DefaultCompletionTokens;
    private bool _hasData;

    public GenerationLengthModel(string path)
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
    /// The current estimated typical completion length (tokens): the rolling median when data
    /// exists, otherwise the conservative default. This is the value the ETA / progress bar
    /// uses as the "total" to estimate against.
    /// </summary>
    public int EstimatedCompletionTokens
    {
        get { lock (_lock) { return _hasData ? _median : DefaultCompletionTokens; } }
    }

    /// <summary>
    /// Records the completion token count of a finished turn, refits the median, and persists.
    /// Samples below <see cref="MinFitTokens"/> are ignored (not representative of typical
    /// response length).
    /// </summary>
    public void RecordSample(int completionTokens)
    {
        if (completionTokens < MinFitTokens)
        {
            return;
        }

        lock (_lock)
        {
            _samples.Add(completionTokens);
            while (_samples.Count > MaxSamples)
            {
                _samples.RemoveAt(0);
            }

            Refit();
            Save();
        }
    }

    /// <summary>
    /// Recomputes the rolling median from the retained samples.
    /// </summary>
    private void Refit()
    {
        if (_samples.Count == 0)
        {
            return;
        }

        var sorted = _samples.OrderBy(v => v).ToList();
        _median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
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

            if (root.TryGetProperty("hasData", out var vHasData))
            {
                _hasData = vHasData.GetBoolean();
            }
            if (root.TryGetProperty("median", out var vMedian) && vMedian.TryGetInt32(out var median))
            {
                _median = median;
            }
            if (root.TryGetProperty("samples", out var vSamples) && vSamples.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in vSamples.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var tokens))
                    {
                        _samples.Add(tokens);
                    }
                }
                while (_samples.Count > MaxSamples)
                {
                    _samples.RemoveAt(0);
                }
            }

            // Re-fit from the loaded samples so the persisted median is always consistent with
            // the current logic.
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
                median = _median,
                hasData = _hasData,
                samples = _samples
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
    /// Builds a safe, filesystem-friendly key from a model/variant pair, mirroring
    /// <see cref="PrefillPerformanceModel.BuildKey"/>.
    /// </summary>
    public static string BuildKey(string model, string variant)
        => PrefillPerformanceModel.BuildKey(model, variant);
}