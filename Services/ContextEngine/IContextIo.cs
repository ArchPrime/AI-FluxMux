using System;
using System.IO;

namespace FluxMux.Avalonia.Services.ContextEngine;

/// <summary>
/// The single I/O boundary for the <see cref="IContextEngine"/> module. Every file read/write the
/// engine performs goes through this interface, which keeps the engine free of hard dependencies
/// on the filesystem and makes it trivially testable (a fake <see cref="IContextIo"/> can be
/// injected in unit tests) and portable (a different implementation can target a different
/// storage backend on another platform).
///
/// The default implementation (<see cref="FileSystemContextIo"/>) is the production one: it reads
/// with plain <see cref="File"/> APIs and writes through the shared <see cref="AtomicFile"/>
/// helper so a locked <c>VIBE_STATE.md</c> (held open by an editor or a second instance) can
/// never take the app down.
/// </summary>
public interface IContextIo
{
    /// <summary>
    /// True when <paramref name="path"/> exists and is a file.
    /// </summary>
    bool Exists(string path);

    /// <summary>
    /// Reads the full text at <paramref name="path"/>. Returns <c>null</c> when the file does not
    /// exist; throws on other I/O errors (callers that want resilience should catch).
    /// </summary>
    string? ReadAllText(string path);

    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/> without ever throwing. The
    /// parent directory is created if missing. This is the resilient, atomic write the engine
    /// relies on for <c>VIBE_STATE.md</c>.
    /// </summary>
    void WriteAllTextResilient(string path, string contents);
}

/// <summary>
/// Production <see cref="IContextIo"/> backed by the local filesystem. Reads use plain
/// <see cref="File"/> APIs; writes delegate to the shared <see cref="AtomicFile"/> helper (the
/// single atomic-write implementation for the app).
/// </summary>
public sealed class FileSystemContextIo : IContextIo
{
    public bool Exists(string path)
    {
        return File.Exists(path);
    }

    public string? ReadAllText(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return File.ReadAllText(path);
    }

    public void WriteAllTextResilient(string path, string contents)
    {
        AtomicFile.WriteAllTextResilient(path, contents);
    }
}