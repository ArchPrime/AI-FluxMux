using System;
using System.Globalization;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// The cache strategy to apply to a local llama-server launch.
/// </summary>
public enum LocalCacheStrategy
{
	/// <summary>Let the advisor derive the optimal settings from the model's attributes and VRAM headroom.</summary>
	Auto = 0,

	/// <summary>Full chunk-based prefix shifting (<c>--cache-reuse</c>). Fastest for non-vision, non-recurrent models.</summary>
	Full = 1,

	/// <summary>Exact-prefix cache plus periodic checkpoints (<c>--ctx-checkpoints</c>). Used when vision or MTP/recurrent state disables chunk-based reuse.</summary>
	PrefixCheckpoints = 2,

	/// <summary>No cache args. Fallback / debug.</summary>
	Minimal = 3
}

/// <summary>
/// The concrete cache args derived for a launch.
/// </summary>
public readonly record struct LocalCacheStrategyDecision(
	LocalCacheStrategy Strategy,
	int CacheReuse,
	int CtxCheckpoints,
	string Reason)
{
	public static LocalCacheStrategyDecision None => new(LocalCacheStrategy.Auto, 0, 0, string.Empty);
}

/// <summary>
/// Derives the optimal llama-server cache strategy from a model profile's attributes
/// (vision, MTP/recurrent) and the available VRAM headroom.
///
/// Why this exists: chunk-based cache-reuse (<c>--cache-reuse</c>) is disabled by
/// llama-server when a multimodal projector is loaded, and is less useful when a
/// recurrent/MTP state must be rolled back cleanly. In those cases the optimal
/// strategy is exact-prefix caching plus periodic context checkpoints. For plain
/// text models, full chunk-based reuse is fastest. The strategy is therefore a
/// function of the *model's attributes*, not the hardware, so the same box gets
/// different optimal settings for different models.
///
/// Future-proofing: when a future llama-server build supports cache-reuse with
/// vision, the caller can flip the decision to <see cref="LocalCacheStrategy.Full"/>
/// by detecting the absence of the "cache_reuse is not supported by multimodal"
/// startup warning; the derivation below is the conservative default until then.
/// </summary>
public static class LocalCacheStrategyAdvisor
{
	/// <summary>
	/// Derives the optimal cache strategy for a profile.
	/// </summary>
	/// <param name="profile">The local profile JSON (read for vision, spec type, context).</param>
	/// <param name="visionEnabled">Whether a multimodal projector is loaded for this launch.</param>
	/// <param name="totalVramGiB">Total GPU VRAM in GiB (0 if unknown).</param>
	/// <param name="measuredFootprintGiB">The model's measured or estimated VRAM footprint in GiB (0 if unknown).</param>
	public static LocalCacheStrategyDecision Derive(
		JsonObject? profile,
		bool visionEnabled,
		double totalVramGiB,
		double measuredFootprintGiB)
	{
		// Explicit user override always wins.
		var explicitStrategy = ParseStrategy(GetString(profile, "LocalCacheStrategy"));
		if (explicitStrategy != LocalCacheStrategy.Auto)
		{
			return ApplyExplicit(explicitStrategy, profile);
		}

		var mtp = IsRecurrentOrMtp(profile);

		if (visionEnabled || mtp)
		{
			// Vision disables chunk-based reuse; MTP wants clean recurrent-state rollbacks.
			// Use exact-prefix cache + a fixed, conservative checkpoint count.
			//
			// Why fixed (not sized to live VRAM headroom): at the time the user picks
			// model settings the model is not yet loaded, so any "free VRAM" reading is
			// stale or missing. A fixed 32 is safe across the range of models: enough
			// for clean rollbacks, small enough not to push a tight-VRAM setup over the
			// edge. llama-server itself handles the actual VRAM pressure (spill/OOM);
			// the advisor's job is to pick the strategy, not micro-optimize the count.
			const int checkpoints = 32;

			var reason = visionEnabled && mtp
				? "vision + MTP: chunk-reuse disabled, checkpoints for clean rollbacks"
				: visionEnabled
					? "vision: chunk-reuse disabled, checkpoints for clean rollbacks"
					: "MTP/recurrent: checkpoints for clean state rollbacks";
			return new LocalCacheStrategyDecision(LocalCacheStrategy.PrefixCheckpoints, 0, checkpoints, reason);
		}

		// Plain text model: full chunk-based reuse is fastest.
		int reuse = ParseInt(GetString(profile, "LocalCacheReuse"), 256);
		return new LocalCacheStrategyDecision(LocalCacheStrategy.Full, reuse > 0 ? reuse : 256, 0, "text model: full chunk-based cache-reuse");
	}

	/// <summary>
	/// Resolves the args for an explicit (non-Auto) user choice, honoring the profile's
	/// stored values where sensible.
	/// </summary>
	private static LocalCacheStrategyDecision ApplyExplicit(LocalCacheStrategy strategy, JsonObject? profile)
	{
		switch (strategy)
		{
			case LocalCacheStrategy.Full:
				var reuse = ParseInt(GetString(profile, "LocalCacheReuse"), 256);
				return new LocalCacheStrategyDecision(strategy, reuse > 0 ? reuse : 256, 0, "user: full chunk-based cache-reuse");

			case LocalCacheStrategy.PrefixCheckpoints:
				var checkpoints = ParseInt(GetString(profile, "LocalCtxCheckpoints"), 32);
				return new LocalCacheStrategyDecision(strategy, 0, checkpoints > 0 ? checkpoints : 32, "user: prefix + checkpoints");

			case LocalCacheStrategy.Minimal:
				return new LocalCacheStrategyDecision(strategy, 0, 0, "user: minimal (no cache args)");

			default:
				return LocalCacheStrategyDecision.None;
		}
	}

	private static bool IsRecurrentOrMtp(JsonObject? profile)
	{
		var specType = GetString(profile, "LocalSpecType") ?? string.Empty;
		return specType.Contains("mtp", StringComparison.OrdinalIgnoreCase)
			|| specType.Contains("recurrent", StringComparison.OrdinalIgnoreCase)
			|| specType.Contains("draft", StringComparison.OrdinalIgnoreCase);
	}

	private static LocalCacheStrategy ParseStrategy(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return LocalCacheStrategy.Auto;
		}

		return Enum.TryParse<LocalCacheStrategy>(text.Trim(), ignoreCase: true, out var parsed)
			? parsed
			: LocalCacheStrategy.Auto;
	}

	private static string GetString(JsonObject? profile, string key)
		=> profile?[key]?.ToString() ?? string.Empty;

	private static int ParseInt(string? text, int fallback)
		=> int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
}