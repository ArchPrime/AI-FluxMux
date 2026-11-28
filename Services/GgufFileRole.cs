using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// What a GGUF is for, read from its own contents rather than its name (28 Nov): a model, a vision
/// projector (architecture <c>clip</c>), or a speculative-decoding drafter. A drafter has no token
/// embeddings of its own — it borrows the target model's (Unsloth's DSpark drafter for Inkling 2
/// Mini: architecture <c>dflash</c>, Markov head tensors <c>markov_w1</c> etc.) — so it cannot run
/// alone and is left out of the model list. Results are cached per file length and write time.
/// </summary>
public static class GgufFileRole
{
    public sealed record Info(string Architecture, bool HasTokenEmbedding, bool HasMarkovHead, long Length)
    {
        public bool IsProjector => Architecture.Equals("clip", StringComparison.OrdinalIgnoreCase);

        /// <summary>A drafter: not a projector and no token embeddings of its own.</summary>
        public bool IsDrafter => !IsProjector && !HasTokenEmbedding && Architecture.Length > 0;
    }

    private static readonly ConcurrentDictionary<string, (long Length, DateTime Written, Info? Info)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True for a projector or drafter: a file that goes with a model rather than being one.</summary>
    public static bool IsSidecar(string? path)
    {
        if (LocalHealthGuidance.IsVisionProjectorFile(path))
        {
            return true;
        }

        var info = Read(path);
        return info is not null && (info.IsProjector || info.IsDrafter);
    }

    public static Info? Read(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            var file = new FileInfo(path);
            if (Cache.TryGetValue(file.FullName, out var cached) && cached.Length == file.Length && cached.Written == file.LastWriteTimeUtc)
            {
                return cached.Info;
            }

            var info = ReadUncached(file);
            Cache[file.FullName] = (file.Length, file.LastWriteTimeUtc, info);
            return info;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The llama-server speculative type a drafter runs as, from the types this build lists:
    /// <c>draft-dspark</c> for a drafter with a Markov head, else <c>draft-&lt;architecture&gt;</c>;
    /// null when the build lists neither.
    /// </summary>
    public static string? SpecTypeFor(Info drafter, IReadOnlyCollection<string> serverTypes)
    {
        if (!drafter.IsDrafter)
        {
            return null;
        }

        bool Listed(string type) => serverTypes.Contains(type, StringComparer.OrdinalIgnoreCase);
        if (drafter.HasMarkovHead && Listed("draft-dspark"))
        {
            return "draft-dspark";
        }

        var byArchitecture = "draft-" + drafter.Architecture.ToLowerInvariant();
        return Listed(byArchitecture) ? byArchitecture : null;
    }

    private static Info? ReadUncached(FileInfo file)
    {
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (reader.ReadUInt32() != 0x46554747u)
        {
            return null; // not "GGUF"
        }

        var version = reader.ReadUInt32();
        if (version < 2)
        {
            return null;
        }

        var tensorCount = reader.ReadUInt64();
        var metadataCount = reader.ReadUInt64();
        var architecture = string.Empty;
        var markov = false;
        for (ulong i = 0; i < metadataCount; i++)
        {
            var key = ReadString(reader);
            var type = reader.ReadUInt32();
            if (key == "general.architecture" && type == 8)
            {
                architecture = ReadString(reader);
            }
            else if (key.EndsWith(".has_markov_context", StringComparison.Ordinal) && type == 7)
            {
                markov |= reader.ReadByte() != 0;
            }
            else
            {
                Skip(reader, type);
            }
        }

        var tokenEmbedding = false;
        for (ulong i = 0; i < tensorCount && i < 100_000; i++)
        {
            var name = ReadString(reader);
            var dims = reader.ReadUInt32();
            stream.Seek(8L * dims + 4 + 8, SeekOrigin.Current); // dims, type, offset
            tokenEmbedding |= name.Equals("token_embd.weight", StringComparison.Ordinal);
            markov |= name.StartsWith("markov_", StringComparison.Ordinal) || name.Contains(".markov_", StringComparison.Ordinal);
        }

        return new Info(architecture, tokenEmbedding, markov, file.Length);
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadUInt64();
        if (length > 1 << 20)
        {
            throw new InvalidDataException("GGUF string too long");
        }

        return Encoding.UTF8.GetString(reader.ReadBytes((int)length));
    }

    private static readonly int[] FixedSizes = [1, 1, 2, 2, 4, 4, 4, 1, 0, 0, 8, 8, 8];

    private static void Skip(BinaryReader reader, uint type)
    {
        if (type == 8)
        {
            var length = reader.ReadUInt64();
            reader.BaseStream.Seek((long)length, SeekOrigin.Current);
        }
        else if (type == 9)
        {
            var elementType = reader.ReadUInt32();
            var count = reader.ReadUInt64();
            if (elementType == 8 || elementType == 9)
            {
                for (ulong i = 0; i < count; i++)
                {
                    Skip(reader, elementType);
                }
            }
            else
            {
                reader.BaseStream.Seek((long)count * FixedSizes[elementType], SeekOrigin.Current);
            }
        }
        else if (type < FixedSizes.Length && FixedSizes[type] > 0)
        {
            reader.BaseStream.Seek(FixedSizes[type], SeekOrigin.Current);
        }
        else
        {
            throw new InvalidDataException("unknown GGUF type " + type);
        }
    }
}
