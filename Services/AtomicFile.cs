using System;
using System.IO;
using System.Threading;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Shared, never-throwing atomic file write. Writes to a temp file, retries while the target
/// is locked by another process, then atomically replaces it; on final failure it swallows the
/// error rather than propagating it.
///
/// This is the single atomic-write implementation for the app. It was extracted from
/// <c>FluxMuxConfigService</c> so that other modules (e.g. <c>Services/ContextEngine/</c>) can
/// reuse it instead of each re-implementing the temp-file + retry + move dance.
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/> without ever throwing.
    /// The target can be held open by another process (a second AI-FluxMux instance, an editor,
    /// or a file watcher). A failed write must never take the app down, so this writes to a temp
    /// file, retries while the target is locked, then atomically replaces it. On final failure it
    /// swallows the error rather than propagating it.
    /// </summary>
    public static void WriteAllTextResilient(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.WriteAllText(tempPath, contents);
                File.Move(tempPath, path, overwrite: true);
                return;
            }
            catch (IOException)
            {
                // Target (or temp) is locked by another process; back off and retry.
            }
            catch (UnauthorizedAccessException)
            {
                // Same class of failure on some setups; retry.
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // Best effort; a stray .tmp file is harmless.
                }
            }

            Thread.Sleep(120 * (attempt + 1));
        }

        // Give up quietly. Losing one save is acceptable; crashing the whole app over a
        // locked file is not.
    }
}