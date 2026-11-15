using System.Diagnostics;
using System.IO;

namespace LandingRecorder;

/// <summary>
/// Minimal static file logger writing app.log next to whichever executable is running (main app or
/// VideoCaptureManualTest). The log is kept across runs (each run starts with a "started" marker line) and
/// rotated by size, see <see cref="LogRotation"/>. Logging never throws.
/// </summary>
public static class AppLogger
{
    /// <summary>app.log is rotated before a write once it has grown beyond this size.</summary>
    public const long MaxLogBytes = 1024 * 1024;

    /// <summary>At most this many numbered archives (app-1.log .. app-5.log) are kept.</summary>
    public const int MaxArchivedLogs = 5;

    private const string LogName = "app.log";
    private static readonly object SyncRoot = new();
    private static readonly string LogPath;

    static AppLogger()
    {
        var directory = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        directory = directory is null ? string.Empty : Path.GetDirectoryName(directory);
        LogPath = Path.Combine(directory ?? string.Empty, LogName);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        WriteLine($"===== {Process.GetCurrentProcess().ProcessName} started (pid {Environment.ProcessId}) =====");
    }

    public static void WriteLine(string message)
    {
        lock (SyncRoot)
        {
            try
            {
                if (new FileInfo(LogPath) is { Exists: true, Length: > MaxLogBytes })
                {
                    LogRotation.Rotate(LogPath, MaxArchivedLogs);
                }
            }
            catch (Exception)
            {
                // Rotation failed (e.g. an archive is locked by another process): keep appending to the
                // current log; rotation is retried on the next write.
            }

            try
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch (Exception)
            {
                // A logger must never take the app down (e.g. the file is briefly locked by another instance).
            }
        }
    }
}

/// <summary>
/// Size-based log rotation with a fixed number of numbered archives, oldest first: for "app.log" the archives
/// are app-1.log (oldest) .. app-N.log (newest). Separate from <see cref="AppLogger"/> so it can be tested
/// without triggering the logger's static initialization.
/// </summary>
internal static class LogRotation
{
    /// <summary>
    /// Moves <paramref name="logPath"/> to the next archive number, so the next write starts a new log.
    /// Existing archives keep their relative order and are compacted to 1..k first (in case one was deleted
    /// by hand); if all <paramref name="maxArchives"/> numbers are taken, the oldest (number 1) is deleted and
    /// the rest shift down by one, so the newest archive is always the highest number.
    /// </summary>
    public static void Rotate(string logPath, int maxArchives)
    {
        var directory = Path.GetDirectoryName(logPath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(logPath);
        var extension = Path.GetExtension(logPath);
        string ArchivePath(int number) => Path.Combine(directory, $"{baseName}-{number}{extension}");

        // Oldest first (ascending number).
        var archives = Enumerable.Range(1, maxArchives).Select(ArchivePath).Where(File.Exists).ToList();
        if (archives.Count >= maxArchives)
        {
            File.Delete(archives[0]);
            archives.RemoveAt(0);
        }

        // Ascending order guarantees each target number has already been vacated.
        for (var i = 0; i < archives.Count; i++)
        {
            var target = ArchivePath(i + 1);
            if (!string.Equals(archives[i], target, StringComparison.OrdinalIgnoreCase))
            {
                File.Move(archives[i], target);
            }
        }

        File.Move(logPath, ArchivePath(archives.Count + 1));
    }
}
