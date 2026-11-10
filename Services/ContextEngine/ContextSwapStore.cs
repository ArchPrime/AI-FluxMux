using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services.ContextEngine;

/// <summary>
/// The paged-context "swap store": when forward-prune truncates an oversized older tool result,
/// the full body is spilled to disk here and the prompt carries only a short pointer
/// (<c>swap_id</c> + byte count + page size). The model can later read back any page of the
/// spilled body via the <c>read_swap_log</c> MCP tool, so the "lossy" stub becomes lossless.
///
/// Design goals (from the plan):
///   * <b>Atomic spill</b> — writes go through the shared <see cref="IContextIo"/> seam (which
///     delegates to <see cref="AtomicFile"/>), so a locked or failing write never breaks a turn.
///   * <b>Paginated read</b> — <see cref="ReadPage"/> returns a bounded slice so the model can
///     page through a large body without re-injecting it all at once.
///   * <b>Purge-by-age</b> — <see cref="PurgeOlderThan"/> removes entries older than a cutoff so
///     the store does not grow without bound.
///   * <b>Never throws</b> — every public method catches its own errors and returns a safe
///     default; a swap-store failure must never take the app down.
///
/// The store is a single JSON file (<c>context_swap.json</c>) next to the config, shaped as
/// <c>{ "entries": { "swap_id": { "body": "...", "bytes": N, "createdUtc": "..." } } }</c>.
/// </summary>
public sealed class ContextSwapStore
{
    private const int DefaultPageSizeChars = 8_000;
    private const int MaxBodyChars = 512_000; // hard cap per entry so one spill cannot balloon the file

    private readonly IContextIo _io;
    private readonly string _path;

    public ContextSwapStore(IContextIo io, string path)
    {
        _io = io ?? throw new ArgumentNullException(nameof(io));
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    /// <summary>
    /// Spills <paramref name="body"/> to the store and returns a stable <c>swap_id</c>. The id is
    /// derived from the body's content hash so re-spilling the same body is idempotent (no
    /// duplicate entries). Returns an empty string on failure (never throws).
    /// </summary>
    public string Spill(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        try
        {
            var id = "swap_" + ComputeStableHash(body);
            var doc = LoadDoc();
            var entries = GetOrCreateEntries(doc);

            // Idempotent: if the same id already exists with the same body, do nothing.
            if (entries[id] is JsonObject existing && existing["body"]?.ToString() == body)
            {
                return id;
            }

            var truncated = body.Length > MaxBodyChars ? body[..MaxBodyChars] : body;
            entries[id] = new JsonObject
            {
                ["body"] = truncated,
                ["bytes"] = truncated.Length,
                ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            };

            SaveDoc(doc);
            return id;
        }
        catch
        {
            // A failed spill must never break a turn; the caller falls back to the lossy stub.
            return string.Empty;
        }
    }

    /// <summary>
    /// Reads a page of the spilled body for <paramref name="swapId"/>. Returns
    /// <c>(text, totalBytes, hasMore)</c>. <paramref name="page"/> is 0-based;
    /// <paramref name="pageSize"/> defaults to <see cref="DefaultPageSizeChars"/>. Returns an
    /// empty result when the id is unknown or the read fails (never throws).
    /// </summary>
    public (string Text, int TotalBytes, bool HasMore) ReadPage(
        string swapId,
        int page = 0,
        int pageSize = DefaultPageSizeChars)
    {
        if (string.IsNullOrWhiteSpace(swapId) || page < 0)
        {
            return (string.Empty, 0, false);
        }

        try
        {
            var doc = LoadDoc();
            if (doc.Root["entries"] is not JsonObject entries
                || entries[swapId] is not JsonObject entry
                || entry["body"]?.ToString() is not string body)
            {
                return (string.Empty, 0, false);
            }

            var size = pageSize > 0 ? pageSize : DefaultPageSizeChars;
            var start = page * size;
            if (start >= body.Length)
            {
                return (string.Empty, body.Length, false);
            }

            var end = Math.Min(start + size, body.Length);
            var slice = body[start..end];
            return (slice, body.Length, end < body.Length);
        }
        catch
        {
            return (string.Empty, 0, false);
        }
    }

    /// <summary>
    /// Removes all entries created before <paramref name="cutoffUtc"/>. Returns the number of
    /// entries removed. Never throws.
    /// </summary>
    public int PurgeOlderThan(DateTimeOffset cutoffUtc)
    {
        try
        {
            var doc = LoadDoc();
            if (doc.Root["entries"] is not JsonObject entries)
            {
                return 0;
            }

            var removed = 0;
            var toRemove = new List<string>();
            foreach (var (key, value) in entries)
            {
                if (value is JsonObject entry
                    && entry["createdUtc"]?.ToString() is string ts
                    && DateTimeOffset.TryParse(ts, out var created)
                    && created < cutoffUtc)
                {
                    toRemove.Add(key);
                    removed++;
                }
            }

            if (removed > 0)
            {
                foreach (var key in toRemove)
                {
                    entries.Remove(key);
                }

                SaveDoc(doc);
            }

            return removed;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Ensures the store file exists (creating an empty one if missing). Called at startup so the
    /// file is present and the model can see it. Never throws.
    /// </summary>
    public void EnsureExists()
    {
        try
        {
            if (!_io.Exists(_path))
            {
                _io.WriteAllTextResilient(_path, new JsonObject { ["entries"] = new JsonObject() }.ToJsonString());
            }
        }
        catch
        {
            // Best effort; a missing file is handled gracefully by LoadDoc.
        }
    }

    private JsonObject LoadDoc()
    {
        if (!_io.Exists(_path))
        {
            return new JsonObject { ["entries"] = new JsonObject() };
        }

        var text = _io.ReadAllText(_path);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject { ["entries"] = new JsonObject() };
        }

        try
        {
            var parsed = JsonNode.Parse(text) as JsonObject;
            if (parsed is null)
            {
                return new JsonObject { ["entries"] = new JsonObject() };
            }

            if (parsed["entries"] is not JsonObject)
            {
                parsed["entries"] = new JsonObject();
            }

            return parsed;
        }
        catch
        {
            // Corrupt file: start fresh rather than crash.
            return new JsonObject { ["entries"] = new JsonObject() };
        }
    }

    private static JsonObject GetOrCreateEntries(JsonObject doc)
    {
        if (doc["entries"] is not JsonObject entries)
        {
            entries = new JsonObject();
            doc["entries"] = entries;
        }

        return entries;
    }

    private void SaveDoc(JsonObject doc)
    {
        _io.WriteAllTextResilient(_path, doc.ToJsonString());
    }

    private static string ComputeStableHash(string body)
    {
        // A simple, stable, non-cryptographic hash (FNV-1a) over the body. Good enough for
        // idempotency; not a security boundary.
        unchecked
        {
            const uint fnvPrime = 16777619;
            uint hash = 2166136261;
            foreach (var c in body)
            {
                hash ^= c;
                hash *= fnvPrime;
            }

            return hash.ToString("x8");
        }
    }
}