using System.Diagnostics;
using System.IO;

namespace LandingRecorder.Services;

public sealed class VideoCaptureService : IDisposable
{
    private WindowVideoRecorder? _videoRecorder;
    private string _simProcessName = "Microsoft Flight Simulator";

    /// <summary>
    /// Matched against each process's name (exact, case-insensitive) or main window title (substring).
    /// Changing it drops the current recorder so the next <see cref="EnsureInitialized"/> re-locates the window.
    /// Must not be changed while recording.
    /// </summary>
    public string SimProcessName
    {
        get => _simProcessName;
        set
        {
            if (string.Equals(_simProcessName, value, StringComparison.Ordinal))
            {
                return;
            }

            _simProcessName = value;
            DropRecorder();
        }
    }

    public bool IsRecording => _videoRecorder?.IsRecording ?? false;

    /// <summary>
    /// Makes sure there's a recorder bound to a live simulator window. A cached recorder whose window no
    /// longer exists (MSFS restarted, or its main window was recreated) is dropped and the window is looked
    /// up again, so a stale handle can't make every later recording fail. Must not be called while recording.
    /// </summary>
    public bool EnsureInitialized()
    {
        if (_videoRecorder is not null)
        {
            // Also called on SimConnect (re)connect, which can happen mid-recording: never drop an active recorder.
            if (_videoRecorder.IsRecording || IsWindow(_videoRecorder.WindowHandle))
            {
                return true;
            }

            AppLogger.WriteLine("Simulator window used for video capture no longer exists; looking it up again.");
            DropRecorder();
        }

        if (FindSimulatorWindow() is not var (windowHandle, processId, processName, title))
        {
            AppLogger.WriteLine($"Simulator window matching '{SimProcessName}' was not found; video capture is unavailable.");
            return false;
        }

        AppLogger.WriteLine($"Video capture target: '{title}' (process {processName}, pid {processId}).");
        _videoRecorder = new WindowVideoRecorder(windowHandle, processId);
        return true;
    }

    /// <summary>
    /// Finds the simulator's main window. An exact (case-insensitive) process-name match wins over a
    /// window-title substring match, so e.g. a browser tab titled "...Microsoft Flight Simulator..." can't be
    /// picked over the sim itself when the setting is the sim's process name.
    /// </summary>
    private (IntPtr WindowHandle, int ProcessId, string ProcessName, string Title)? FindSimulatorWindow()
    {
        (IntPtr, int, string, string)? titleMatch = null;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var windowHandle = process.MainWindowHandle;
                    if (windowHandle == IntPtr.Zero || process.Id == Environment.ProcessId)
                    {
                        continue;
                    }

                    var candidate = (windowHandle, process.Id, process.ProcessName, process.MainWindowTitle);
                    if (string.Equals(process.ProcessName, SimProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }

                    if (titleMatch is null && process.MainWindowTitle.Contains(SimProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        titleMatch = candidate;
                    }
                }
                catch (Exception)
                {
                    // Process exited during enumeration, or access denied; skip it.
                }
            }
        }

        return titleMatch;
    }

    private void DropRecorder()
    {
        try
        {
            _videoRecorder?.Dispose();
        }
        catch (Exception ex)
        {
            AppLogger.WriteLine($"Error disposing video recorder: {ex.Message}");
        }

        _videoRecorder = null;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    /// <summary>Timestamped recording file path in <paramref name="folder"/>, e.g. flight-20261115-143000.mp4.</summary>
    public static string CreateOutputPath(string folder, string extension) =>
        Path.Combine(folder, $"flight-{DateTime.Now:yyyyMMdd-HHmmss}{extension}");

    public string? Start(string folder, int framesPerSecond = 60)
    {
        if (_videoRecorder is null || _videoRecorder.IsRecording)
        {
            return null;
        }

        try
        {
            var outputPath = CreateOutputPath(folder, ".mp4");
            _videoRecorder.Start(outputPath, framesPerSecond);
            AppLogger.WriteLine($"Video recording started: {outputPath}");
            return outputPath;
        }
        catch (Exception ex)
        {
            // Drop the recorder so the next recording looks the window up again instead of retrying a
            // recorder that may be bound to a dead/wrong window. (WindowVideoRecorder.Start already released
            // its own partially-created resources before rethrowing.)
            AppLogger.WriteLine($"Failed to start video recording: {ex.Message}");
            DropRecorder();
            return null;
        }
    }

    public void Stop()
    {
        if (_videoRecorder is null || !_videoRecorder.IsRecording)
        {
            return;
        }

        _videoRecorder.Stop();
        AppLogger.WriteLine("Video recording stopped.");
    }

    public void Dispose()
    {
        _videoRecorder?.Dispose();
        _videoRecorder = null;
    }
}
