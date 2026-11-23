using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// An append-only text log that is written off the calling thread, stamps each line with the
/// date as well as the time, and rotates by size.
///
/// Why each part matters:
/// - Background writing: the gateway used <c>File.AppendAllText</c> under a lock on the request
///   path, reopening the file for every line (~26 lines per chat turn, plus repeats).
/// - Date in the stamp: lines were "[HH:mm:ss]" only, so yesterday's 20:48 was indistinguishable
///   from today's 20:48 when reading back a long-lived log — which misled a diagnosis.
/// - Rotation: Proxy_Cloud_Handshake.log had grown to 65.8 MB and never shrank. At
///   <see cref="MaxBytes"/> the file becomes "&lt;name&gt;.1" (the previous .1 becomes .2, the
///   oldest is dropped), so at most ~3× the limit is kept.
///
/// One instance per path (<see cref="For"/>), so all writers to a file share one queue and one
/// order. Never throws to callers; a failed write is dropped (logging must not break a turn).
/// </summary>
public sealed class RollingLogFile
{
    /// <summary>Size at which the active file is rotated.</summary>
    public const long MaxBytes = 20L * 1024 * 1024;

    /// <summary>Rotated generations kept beside the active file (name.1 … name.N).</summary>
    public const int KeptGenerations = 2;

    private static readonly ConcurrentDictionary<string, RollingLogFile> Instances = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _path;
    private readonly long _maxBytes;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), boundedCapacity: 50_000);
    private readonly Thread _writer;
    private int _pending; // queued but not yet on disk

    private RollingLogFile(string path, long maxBytes)
    {
        _path = path;
        _maxBytes = maxBytes;
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "FluxMux log: " + Path.GetFileName(path) };
        _writer.Start();
    }

    /// <summary>The shared writer for <paramref name="path"/>.</summary>
    public static RollingLogFile For(string path) => Instances.GetOrAdd(Path.GetFullPath(path), p => new RollingLogFile(p, MaxBytes));

    /// <summary>Test hook: a private writer with a custom size limit.</summary>
    internal static RollingLogFile CreateForTest(string path, long maxBytes) => new(path, maxBytes);

    /// <summary>"[yyyy-MM-dd HH:mm:ss] " for <paramref name="now"/>.</summary>
    public static string Stamp(DateTime now) => "[" + now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "] ";

    /// <summary>Queues one stamped line. Returns immediately.</summary>
    public void WriteLine(string message)
    {
        // TryAdd: if the writer has fallen 50,000 lines behind, drop rather than block a request.
        Interlocked.Increment(ref _pending);
        if (!_queue.TryAdd(Stamp(DateTime.Now) + message))
        {
            Interlocked.Decrement(ref _pending);
        }
    }

    /// <summary>Blocks until everything queued so far is on disk (tests, shutdown).</summary>
    public void Flush(TimeSpan timeout)
    {
        // Counts lines still in flight, including one the writer has taken but not yet written.
        var deadline = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref _pending) > 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }
    }

    private void WriteLoop()
    {
        foreach (var first in _queue.GetConsumingEnumerable())
        {
            var taken = 1;
            lock (_queue)
            {
                try
                {
                    var batch = new StringBuilder(first).AppendLine();
                    var lines = 1;
                    while (batch.Length < 256 * 1024 && _queue.TryTake(out var next))
                    {
                        batch.AppendLine(next);
                        lines++;
                    }

                    taken = lines;

                    RotateIfNeeded();
                    using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    var bytes = Encoding.UTF8.GetBytes(batch.ToString());
                    stream.Write(bytes, 0, bytes.Length);
                }
                catch
                {
                    // Locked, read-only or missing folder: drop this batch, keep the app running.
                }
                finally
                {
                    Interlocked.Add(ref _pending, -taken);
                }
            }
        }
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < _maxBytes)
        {
            return;
        }

        for (var generation = KeptGenerations; generation >= 1; generation--)
        {
            var from = generation == 1 ? _path : _path + "." + (generation - 1).ToString(CultureInfo.InvariantCulture);
            var to = _path + "." + generation.ToString(CultureInfo.InvariantCulture);
            if (File.Exists(from))
            {
                File.Move(from, to, overwrite: true);
            }
        }
    }
}
