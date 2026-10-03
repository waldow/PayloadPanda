using System.Collections.Concurrent;
using System.IO;

namespace PayloadPanda.Services;

// Writes files via a temp file + atomic rename so a crash mid-write can never
// leave a half-written (corrupt) file at the real path. Writes to the same path
// are serialized, and each write uses its own temp file, so overlapping saves
// (a debounced session save racing the save on exit) can't trample each other.
internal static class AtomicFile
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_locks =
        new(StringComparer.OrdinalIgnoreCase);

    public static async Task WriteAllTextAsync(string path, string contents)
    {
        var fullPath = Path.GetFullPath(path);
        var gate = s_locks.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));
        var tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await File.WriteAllTextAsync(tempPath, contents).ConfigureAwait(false);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stray temp file is harmless; the original exception is what matters.
        }
    }
}
