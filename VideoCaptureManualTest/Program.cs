using System.Diagnostics;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LandingRecorder;
using SharpDX.MediaFoundation;

const uint RoInitMultiThreaded = 1;
const int RpcEChangedMode = unchecked((int)0x80010106);

var playAfterRecording = args.Contains("--play");

var roResult = RoInitialize(RoInitMultiThreaded);
if (roResult != 0 && roResult != 1 && roResult != RpcEChangedMode)
{
    Marshal.ThrowExceptionForHR(roResult);
}

MediaFactory.Startup(MediaFactory.Version, 0);

try
{
    RunTest(playAfterRecording);
}
finally
{
    MediaFactory.Shutdown();
    RoUninitialize();
}

static void RunTest(bool playAfterRecording)
{
    // A plain Form in this same process is both the video capture target (a genuine HWND to
    // move/resize for the ring-buffer stress test below) and, via its own process id, the
    // WASAPI process-loopback audio capture target - a looped system sound played from here
    // exercises the audio path end to end instead of leaving it untested (Paint has no audio
    // session worth capturing).
    using var window = new Form
    {
        Width = 900,
        Height = 700,
        Text = "VideoCaptureManualTest harness",
        StartPosition = FormStartPosition.Manual,
        Location = new System.Drawing.Point(100, 100)
    };
    window.Show();
    Application.DoEvents();

    var hwnd = window.Handle;
    var selfPid = Environment.ProcessId;
    Console.WriteLine($"Harness window hwnd = {hwnd}, pid = {selfPid}");

    using var soundPlayer = new SoundPlayer(@"C:\Windows\Media\Alarm01.wav");
    var stopSound = false;
    var soundTask = Task.Run(() =>
    {
        while (!stopSound)
        {
            soundPlayer.PlaySync();
        }
    });

    // Two Start/Stop cycles on the SAME recorder instance, exactly like the app does across consecutive
    // landings - state left over from one recording (e.g. the audio/video zero-point) must not leak into
    // the next. Each output's duration is checked against the wall-clock recording time, since stale
    // timestamps show up as an inflated duration.
    // app.log is kept across runs now, so only lines written by this run are checked for errors.
    var runStartTime = DateTime.Now.AddSeconds(-1);
    const int cycleCount = 2;
    var cycleDuration = TimeSpan.FromSeconds(6);
    var recorder = new WindowVideoRecorder(hwnd, selfPid);
    var outputs = new List<string>();
    var moveIndex = 0;

    for (var cycle = 1; cycle <= cycleCount; cycle++)
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, $"harness-{DateTime.Now:yyyyMMdd-HHmmss}-{cycle}.mp4");
        outputs.Add(outputPath);

        Console.WriteLine($"[cycle {cycle}] Starting capture...");
        recorder.Start(outputPath, 60);
        Console.WriteLine($"[cycle {cycle}] Recording: {recorder.Width}x{recorder.Height} -> {outputPath}");

        // Move/resize continuously to stress the ring-buffer slot recycling under load.
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < cycleDuration)
        {
            var x = 100 + moveIndex % 10 * 20;
            var y = 100 + moveIndex % 7 * 15;
            MoveWindow(hwnd, x, y, 900 + moveIndex % 5 * 20, 700, true);
            Application.DoEvents();
            Thread.Sleep(80);
            moveIndex++;
        }

        recorder.Stop();
        Console.WriteLine($"[cycle {cycle}] Stopped.");

        // Gap between recordings, like taxi/climb-out between landings; audio keeps playing meanwhile.
        if (cycle < cycleCount)
        {
            var gap = Stopwatch.StartNew();
            while (gap.Elapsed < TimeSpan.FromSeconds(2))
            {
                Application.DoEvents();
                Thread.Sleep(50);
            }
        }
    }

    recorder.Dispose();

    stopSound = true;
    soundPlayer.Stop();
    soundTask.Wait(TimeSpan.FromSeconds(2));
    window.Close();

    var allOk = true;
    foreach (var outputPath in outputs)
    {
        Console.WriteLine($"--- {Path.GetFileName(outputPath)} ---");
        var info = new FileInfo(outputPath);
        if (!info.Exists)
        {
            Console.WriteLine("Output file was NOT created.");
            allOk = false;
            continue;
        }

        Console.WriteLine($"Output file exists: {info.Length} bytes" + (info.Length < 10_000 ? " (WARNING: suspiciously small)" : ""));
        allOk &= CheckDuration(outputPath, cycleDuration);
        allOk &= AnalyzeAudio(outputPath);
    }

    var logPath = Path.Combine(AppContext.BaseDirectory, "app.log");
    if (File.Exists(logPath))
    {
        var errorLines = File.ReadAllLines(logPath)
            .Where(line => LogLineTime(line) is DateTime time && time >= runStartTime)
            .Where(line => line.Contains("Error", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Console.WriteLine(errorLines.Count == 0 ? "No errors in app.log." : $"WARNING: {errorLines.Count} error line(s) in app.log, e.g.: {errorLines[0]}");
        allOk &= errorLines.Count == 0;
    }

    Console.WriteLine(allOk ? "RESULT: all checks passed." : "RESULT: some checks FAILED (see above).");

    if (playAfterRecording)
    {
        foreach (var outputPath in outputs.Where(File.Exists))
        {
            Process.Start(new ProcessStartInfo(outputPath) { UseShellExecute = true });
        }
    }
    else
    {
        Console.WriteLine("Play them manually to verify:");
        foreach (var outputPath in outputs)
        {
            Console.WriteLine($"  {outputPath}");
        }
    }
}

/// <summary>
/// The file's duration should be close to the wall-clock recording time; stale per-recording timestamps
/// (e.g. audio stamped against the previous recording's zero-point) inflate it by the time between recordings.
/// </summary>
/// <summary>Timestamp of an AppLogger line ("[yyyy-MM-dd HH:mm:ss.fff] message"), or null.</summary>
static DateTime? LogLineTime(string line) =>
    line.Length > 25 && line[0] == '['
    && DateTime.TryParseExact(line[1..24], "yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.None, out var time)
        ? time
        : null;

static bool CheckDuration(string path, TimeSpan expected)
{
    using var sourceReader = new SourceReader(path);
    var duration = TimeSpan.FromTicks(sourceReader.GetPresentationAttribute(
        SourceReaderIndex.MediaSource, PresentationDescriptionAttributeKeys.Duration));
    var ok = duration > expected - TimeSpan.FromSeconds(2) && duration < expected + TimeSpan.FromSeconds(2);
    Console.WriteLine($"Duration: {duration.TotalSeconds:F2} s (expected ~{expected.TotalSeconds:F0} s)" + (ok ? "" : " WARNING: unexpected duration"));
    return ok;
}

static bool AnalyzeAudio(string path)
{
    using var sourceReader = new SourceReader(path);

    var audioStreamIndex = -1;
    for (var i = 0; i < 4; i++)
    {
        try
        {
            using var mediaType = sourceReader.GetCurrentMediaType(i);
            if (mediaType.Get(MediaTypeAttributeKeys.MajorType) == MediaTypeGuids.Audio)
            {
                audioStreamIndex = i;
                break;
            }
        }
        catch
        {
            break;
        }
    }

    if (audioStreamIndex < 0)
    {
        Console.WriteLine("No audio stream found in the output file.");
        return false;
    }

    var pcmType = new MediaType();
    pcmType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
    pcmType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
    sourceReader.SetCurrentMediaType(audioStreamIndex, pcmType);

    long totalSamples = 0;
    long aboveNoiseFloorSamples = 0;
    short peak = 0;

    while (true)
    {
        var sample = sourceReader.ReadSample(audioStreamIndex, SourceReaderControlFlags.None, out _, out var flags, out _);
        if ((flags & SourceReaderFlags.Endofstream) != 0 || sample is null)
        {
            sample?.Dispose();
            break;
        }

        using var buffer = sample.ConvertToContiguousBuffer();
        var dataPointer = buffer.Lock(out _, out var currentLength);
        try
        {
            var shortCount = currentLength / 2;
            var samples = new short[shortCount];
            Marshal.Copy(dataPointer, samples, 0, shortCount);
            foreach (var s in samples)
            {
                totalSamples++;
                var abs = Math.Abs((int)s);
                if (abs > 50)
                {
                    aboveNoiseFloorSamples++;
                }
                if (abs > peak)
                {
                    peak = (short)abs;
                }
            }
        }
        finally
        {
            buffer.Unlock();
        }
        sample.Dispose();
    }

    Console.WriteLine($"Audio samples decoded: {totalSamples}, above-noise-floor samples: {aboveNoiseFloorSamples}, peak amplitude: {peak}/32767");
    Console.WriteLine(aboveNoiseFloorSamples > 0
        ? "Real (non-silent) audio was captured and decodes back out of the MP4."
        : "WARNING: audio track is present but decodes as silence.");
    return aboveNoiseFloorSamples > 0;
}

[DllImport("combase.dll")]
static extern int RoInitialize(uint initType);

[DllImport("combase.dll")]
static extern void RoUninitialize();

[DllImport("user32.dll")]
static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);
