using System.IO;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading.Channels;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.MediaFoundation;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using D3DDevice = SharpDX.Direct3D11.Device;
using WinRT;

namespace LandingRecorder;

public sealed class WindowVideoRecorder : IDisposable
{
    private const int FramePoolBufferCount = 2;
    private const int RingBufferSlotCount = 4;

    // MFVideoFormat_H264 - not exposed as a named field by SharpDX.MediaFoundation.VideoFormatGuids
    // in this version (only the wrong "H264Es" elementary-stream variant exists there).
    private static readonly Guid MFVideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71");

    private readonly IntPtr _windowHandle;
    private readonly int _targetProcessId;
    private readonly object _captureLock = new();
    private D3DDevice? _d3dDevice;
    private IDirect3DDevice? _winrtDevice;
    private DXGIDeviceManager? _dxgiDeviceManager;
    private Texture2D[]? _ringTextures;
    private Channel<int>? _freeSlotChannel;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _captureSession;
    private SinkWriter? _sinkWriter;
    private int _sinkStreamIndex;
    private int _sinkStreamIndexAudio;
    private bool _audioEnabled;
    private double _framesPerSecond;
    private Channel<PendingFrame>? _frameChannel;
    private Task? _encoderTask;
    private ProcessLoopbackAudioCapture? _audioCapture;
    private Task? _audioEncoderTask;
    private long _frameArrivedCount;
    private long _writtenFrameCount;
    private long _noFreeSlotDropCount;
    private long _channelFullDropCount;
    private long _frameArrivedStartTimestamp;
    private bool _isRecording;
    private bool _disposed;
    private volatile bool _hasFirstFrame;
    private long _firstFrameTimestampTicks;
    private long _lastWrittenFrameTimestampTicks;

    private readonly record struct PendingFrame(int SlotIndex, long SampleTime100ns, long Duration100ns);

    public WindowVideoRecorder(IntPtr windowHandle, int targetProcessId)
    {
        if (windowHandle == IntPtr.Zero)
        {
            throw new ArgumentException("A valid window handle is required.", nameof(windowHandle));
        }

        _windowHandle = windowHandle;
        _targetProcessId = targetProcessId;
    }

    public bool IsRecording => _isRecording;

    /// <summary>The window this recorder captures (fixed for the instance's lifetime).</summary>
    public IntPtr WindowHandle => _windowHandle;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public void Start(string outputPath, double framesPerSecond = 60)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_isRecording)
        {
            throw new InvalidOperationException("Recording is already in progress.");
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("An output path is required.", nameof(outputPath));
        }

        if (framesPerSecond <= 0 || double.IsNaN(framesPerSecond) || double.IsInfinity(framesPerSecond))
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond), "Frames per second must be positive.");
        }

        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new PlatformNotSupportedException("Windows.Graphics.Capture is not supported on this system.");
        }

        // Reset all per-recording timing state BEFORE anything that can produce samples. The audio
        // capture below starts delivering chunks long before Start() returns, and the audio encoder task
        // reads _hasFirstFrame/_firstFrameTimestampTicks; if they still held the previous recording's
        // values, the first chunks of every 2nd+ recording on the same instance were stamped against the
        // old zero-point (non-monotonic, far-future audio timestamps).
        ResetPerRecordingState();

        try
        {
            StartCore(outputPath, framesPerSecond);
        }
        catch
        {
            // Release whatever was created before the failure (device, ring textures, frame pool, audio
            // capture, sink writer) so repeated failed attempts don't leak GPU resources, and so the
            // instance is never left flagged as recording.
            Stop();
            throw;
        }
    }

    private void ResetPerRecordingState()
    {
        _hasFirstFrame = false;
        Interlocked.Exchange(ref _firstFrameTimestampTicks, 0);
        _lastWrittenFrameTimestampTicks = 0;
        Interlocked.Exchange(ref _frameArrivedCount, 0);
        Interlocked.Exchange(ref _writtenFrameCount, 0);
        Interlocked.Exchange(ref _noFreeSlotDropCount, 0);
        Interlocked.Exchange(ref _channelFullDropCount, 0);
        Interlocked.Exchange(ref _frameArrivedStartTimestamp, Stopwatch.GetTimestamp());
    }

    private void StartCore(string outputPath, double framesPerSecond)
    {
        var captureItem = CreateCaptureItemForWindow(_windowHandle);
        var size = captureItem.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            throw new InvalidOperationException("The window has no capturable client area.");
        }

        Width = size.Width;
        Height = size.Height;
        _framesPerSecond = framesPerSecond;

        _d3dDevice = new D3DDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);

        // Required so Media Foundation's internal hardware-transform threads can safely share
        // this device with the capture callback's ImmediateContext use - without this, WriteSample
        // on a DXGI-backed sample hangs indefinitely (confirmed empirically, not merely conventional).
        using (var multithread = _d3dDevice.QueryInterface<Multithread>())
        {
            multithread.SetMultithreadProtected(new SharpDX.Mathematics.Interop.RawBool(true));
        }

        _winrtDevice = CreateDirect3DDevice(_d3dDevice);

        _ringTextures = new Texture2D[RingBufferSlotCount];
        for (var i = 0; i < RingBufferSlotCount; i++)
        {
            _ringTextures[i] = new Texture2D(_d3dDevice, new Texture2DDescription
            {
                Width = Width,
                Height = Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.None,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None
            });
        }

        _freeSlotChannel = Channel.CreateBounded<int>(RingBufferSlotCount);
        for (var i = 0; i < RingBufferSlotCount; i++)
        {
            _freeSlotChannel.Writer.TryWrite(i);
        }

        _dxgiDeviceManager = new DXGIDeviceManager();
        _dxgiDeviceManager.ResetDevice(_d3dDevice);

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            FramePoolBufferCount,
            size);
        _framePool.FrameArrived += FramePool_FrameArrived;

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            var audioCapture = new ProcessLoopbackAudioCapture();
            audioCapture.Start(_targetProcessId);
            _audioCapture = audioCapture;
            _audioEnabled = true;
        }
        catch (Exception ex)
        {
            AppLogger.WriteLine($"Process-specific audio capture unavailable, recording video only: {ex.Message}");
            _audioCapture?.Dispose();
            _audioCapture = null;
            _audioEnabled = false;
        }

        try
        {
            CreateSinkWriter(outputPath, framesPerSecond);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("The MP4 sink writer could not be created.", ex); // Start() cleans up.
        }

        _frameChannel = Channel.CreateBounded<PendingFrame>(new BoundedChannelOptions(RingBufferSlotCount)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
        _encoderTask = Task.Run(() => EncodeFramesAsync(_frameChannel.Reader));
        if (_audioEnabled)
        {
            _audioEncoderTask = Task.Run(() => EncodeAudioAsync(_audioCapture!.Reader));
        }
        _isRecording = true;
        _captureSession = _framePool.CreateCaptureSession(captureItem);
        _captureSession.StartCapture();
    }

    private static long Pack(int hi, int lo) => ((long)hi << 32) | (uint)lo;

    private void CreateSinkWriter(string outputPath, double framesPerSecond)
    {
        var bitrate = (int)Math.Clamp(Width * Height * framesPerSecond * 0.10, 4_000_000, 50_000_000);

        var attributes = new MediaAttributes(3);
        attributes.Set(SinkWriterAttributeKeys.D3DManager, _dxgiDeviceManager);
        attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1);
        attributes.Set(SinkWriterAttributeKeys.DisableThrottling, 1);

        _sinkWriter = MediaFactory.CreateSinkWriterFromURL(outputPath, null, attributes);

        var outputType = new MediaType();
        outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outputType.Set(MediaTypeAttributeKeys.Subtype, MFVideoFormatH264);
        outputType.Set(MediaTypeAttributeKeys.AvgBitrate, bitrate);
        outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
        outputType.Set(MediaTypeAttributeKeys.FrameSize, Pack(Width, Height));
        outputType.Set(MediaTypeAttributeKeys.FrameRate, Pack((int)framesPerSecond, 1));
        outputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
        _sinkWriter.AddStream(outputType, out _sinkStreamIndex);

        var inputType = new MediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Argb32);
        inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
        inputType.Set(MediaTypeAttributeKeys.FrameSize, Pack(Width, Height));
        inputType.Set(MediaTypeAttributeKeys.FrameRate, Pack((int)framesPerSecond, 1));
        _sinkWriter.SetInputMediaType(_sinkStreamIndex, inputType, null);

        if (_audioEnabled)
        {
            var sampleRate = _audioCapture!.SampleRate;
            var channels = _audioCapture.Channels;

            var audioOutputType = new MediaType();
            audioOutputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            audioOutputType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
            audioOutputType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, sampleRate);
            audioOutputType.Set(MediaTypeAttributeKeys.AudioNumChannels, channels);
            audioOutputType.Set(MediaTypeAttributeKeys.AvgBitrate, 192_000);
            _sinkWriter.AddStream(audioOutputType, out _sinkStreamIndexAudio);

            var audioInputType = new MediaType();
            audioInputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            audioInputType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
            audioInputType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, sampleRate);
            audioInputType.Set(MediaTypeAttributeKeys.AudioNumChannels, channels);
            audioInputType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16);
            audioInputType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, channels * 2);
            audioInputType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, sampleRate * channels * 2);
            _sinkWriter.SetInputMediaType(_sinkStreamIndexAudio, audioInputType, null);
        }

        _sinkWriter.BeginWriting();
    }

    private async Task EncodeAudioAsync(ChannelReader<AudioChunk> reader)
    {
        // Audio and video capture start independently, so whichever one's first sample
        // arrives first doesn't yet know the recording's zero-point (set from the first
        // video frame). Audio chunks that arrive before that point are held here and
        // flushed once _hasFirstFrame goes true, rather than dropped or mistimed.
        var pending = new List<AudioChunk>();

        await foreach (var chunk in reader.ReadAllAsync())
        {
            if (!_hasFirstFrame)
            {
                pending.Add(chunk);
                continue;
            }

            if (pending.Count > 0)
            {
                foreach (var queued in pending)
                {
                    WriteAudioChunk(queued);
                }
                pending.Clear();
            }

            WriteAudioChunk(chunk);
        }
    }

    private void WriteAudioChunk(AudioChunk chunk)
    {
        try
        {
            if (_sinkWriter is null)
            {
                return;
            }

            var sampleTime100ns = Math.Max(0, (long)chunk.QpcPosition100ns - Interlocked.Read(ref _firstFrameTimestampTicks));
            var duration100ns = (long)chunk.FrameCount * 10_000_000 / _audioCapture!.SampleRate;

            using var mediaBuffer = MediaFactory.CreateMemoryBuffer(chunk.Pcm16.Length);
            var bufferPointer = mediaBuffer.Lock(out _, out _);
            try
            {
                Marshal.Copy(chunk.Pcm16, 0, bufferPointer, chunk.Pcm16.Length);
            }
            finally
            {
                mediaBuffer.Unlock();
            }
            mediaBuffer.CurrentLength = chunk.Pcm16.Length;

            using var sample = MediaFactory.CreateSample();
            sample.AddBuffer(mediaBuffer);
            sample.SampleTime = sampleTime100ns;
            sample.SampleDuration = duration100ns;
            _sinkWriter.WriteSample(_sinkStreamIndexAudio, sample);
        }
        catch (Exception ex)
        {
            AppLogger.WriteLine($"Error encoding audio chunk: {ex.Message}");
        }
    }

    private void FramePool_FrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (!_isRecording || _frameChannel is null)
        {
            return;
        }

        lock (_captureLock)
        {
            while (true)
            {
                using var frame = sender.TryGetNextFrame();
                if (frame is null)
                {
                    break;
                }

                Interlocked.Increment(ref _frameArrivedCount);

                if (frame.Surface is null)
                {
                    continue;
                }

                // Stamp samples from the frame's own capture timestamp rather than a sequential
                // frame count at a fixed nominal rate - otherwise dropped frames (no free ring
                // slot, or a full encoder channel) silently compress the video's timeline instead
                // of leaving a correctly-timed gap, making the exported MP4 shorter than the real
                // wall-clock recording duration whenever capture can't sustain the requested fps.
                var frameTicks = frame.SystemRelativeTime.Ticks;
                if (!_hasFirstFrame)
                {
                    // Publish the zero-point before the flag, so the audio encoder never sees the flag set
                    // with a stale zero-point.
                    Interlocked.Exchange(ref _firstFrameTimestampTicks, frameTicks);
                    _lastWrittenFrameTimestampTicks = frameTicks;
                    _hasFirstFrame = true;
                }

                if (!_freeSlotChannel!.Reader.TryRead(out var slotIndex))
                {
                    Interlocked.Increment(ref _noFreeSlotDropCount);
                    continue;
                }

                try
                {
                    CopyFrameToRingSlot(frame.Surface, slotIndex);

                    var sampleTime100ns = frameTicks - _firstFrameTimestampTicks;
                    var duration100ns = frameTicks - _lastWrittenFrameTimestampTicks;
                    if (duration100ns <= 0)
                    {
                        duration100ns = (long)(10_000_000 / _framesPerSecond);
                    }

                    var pending = new PendingFrame(slotIndex, sampleTime100ns, duration100ns);
                    if (_frameChannel.Writer.TryWrite(pending))
                    {
                        Interlocked.Increment(ref _writtenFrameCount);
                        _lastWrittenFrameTimestampTicks = frameTicks;
                    }
                    else
                    {
                        Interlocked.Increment(ref _channelFullDropCount);
                        _freeSlotChannel.Writer.TryWrite(slotIndex);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.WriteLine($"Error processing frame: {ex.Message}");
                    _freeSlotChannel.Writer.TryWrite(slotIndex);
                }
            }
        }
    }

    private void CopyFrameToRingSlot(IDirect3DSurface surface, int slotIndex)
    {
        using var texture = GetTexture2DForSurface(surface);
        _d3dDevice!.ImmediateContext.CopyResource(texture, _ringTextures![slotIndex]);
    }

    private static Texture2D GetTexture2DForSurface(IDirect3DSurface surface)
    {
        // CsWinRT projection objects (the Windows SDK projection that comes with the
        // net8.0-windows10.0.19041.0 TFM) are plain managed wrappers around a private
        // WinRT.IObjectReference, not classic COM RCWs, so Marshal.GetIUnknownForObject can't
        // reach their real native pointer (it just mints a fresh CCW around the managed wrapper).
        // WinRT.MarshalInterface<T>.FromManaged is CsWinRT's own (AddRef'd) route to the genuine native pointer; from there,
        // QueryInterface + GetObjectForIUnknown is the same working technique already used
        // by CreateCaptureItemForWindow below.
        var surfacePointer = WinRT.MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var accessIid = typeof(IDirect3DDxgiInterfaceAccess).GUID;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfacePointer, ref accessIid, out var accessPointer));
            try
            {
                var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessPointer);
                var textureIid = typeof(Texture2D).GUID;
                var texturePointer = access.GetInterface(ref textureIid);
                return new Texture2D(texturePointer);
            }
            finally
            {
                Marshal.Release(accessPointer);
            }
        }
        finally
        {
            Marshal.Release(surfacePointer);
        }
    }

    private async Task EncodeFramesAsync(ChannelReader<PendingFrame> reader)
    {
        await foreach (var pending in reader.ReadAllAsync())
        {
            try
            {
                if (_sinkWriter is null)
                {
                    continue;
                }

                MediaFactory.CreateDXGISurfaceBuffer(
                    typeof(Texture2D).GUID,
                    _ringTextures![pending.SlotIndex],
                    0,
                    new SharpDX.Mathematics.Interop.RawBool(false),
                    out var mediaBuffer);

                // CreateDXGISurfaceBuffer leaves CurrentLength at 0 even though MaxLength correctly
                // reflects the surface size - confirmed empirically that WriteSample rejects the
                // sample with E_INVALIDARG unless CurrentLength is set explicitly, same as a plain
                // CPU memory buffer.
                mediaBuffer.CurrentLength = mediaBuffer.MaxLength;

                using (mediaBuffer)
                using (var sample = MediaFactory.CreateSample())
                {
                    sample.AddBuffer(mediaBuffer);
                    sample.SampleTime = pending.SampleTime100ns;
                    sample.SampleDuration = pending.Duration100ns;
                    _sinkWriter.WriteSample(_sinkStreamIndex, sample);
                }
            }
            catch (Exception ex)
            {
                AppLogger.WriteLine($"Error encoding frame: {ex.Message}");
            }
            finally
            {
                _freeSlotChannel!.Writer.TryWrite(pending.SlotIndex);
            }
        }
    }

    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        _isRecording = false;
        _captureSession?.Dispose();
        _captureSession = null;
        if (_framePool is not null)
        {
            _framePool.FrameArrived -= FramePool_FrameArrived;
        }
        _framePool?.Dispose();
        _framePool = null;

        // Wait for any in-flight FrameArrived callback to finish before tearing down shared GPU resources.
        lock (_captureLock)
        {
        }

        var startTimestamp = Interlocked.Read(ref _frameArrivedStartTimestamp);
        var elapsed = startTimestamp == 0
            ? TimeSpan.Zero
            : Stopwatch.GetElapsedTime(startTimestamp);
        var arrivedCount = Interlocked.Read(ref _frameArrivedCount);
        var writtenCount = Interlocked.Read(ref _writtenFrameCount);
        var noSlotCount = Interlocked.Read(ref _noFreeSlotDropCount);
        var fullCount = Interlocked.Read(ref _channelFullDropCount);
        var arrivedPerSecond = elapsed.TotalSeconds > 0
            ? arrivedCount / elapsed.TotalSeconds
            : 0;
        AppLogger.WriteLine(
            $"FrameArrived callbacks: {arrivedCount} over {elapsed.TotalSeconds:F2} s ({arrivedPerSecond:F2} callbacks/s), " +
            $"{writtenCount} written, {noSlotCount} dropped (no free ring slot), {fullCount} dropped (channel full).");

        _frameChannel?.Writer.TryComplete();

        _encoderTask?.GetAwaiter().GetResult();
        _encoderTask = null;
        _frameChannel = null;

        _audioCapture?.Stop();
        _audioEncoderTask?.GetAwaiter().GetResult();
        _audioEncoderTask = null;
        _audioCapture?.Dispose();
        _audioCapture = null;
        _audioEnabled = false;

        try
        {
            _sinkWriter?.Finalize();
        }
        catch (Exception ex)
        {
            AppLogger.WriteLine($"Error finalizing video: {ex.Message}");
        }
        finally
        {
            _sinkWriter?.Dispose();
            _sinkWriter = null;
        }

        _freeSlotChannel = null;
        if (_ringTextures is not null)
        {
            foreach (var texture in _ringTextures)
            {
                texture.Dispose();
            }
            _ringTextures = null;
        }
        _dxgiDeviceManager?.Dispose();
        _dxgiDeviceManager = null;
        _winrtDevice = null;
        _d3dDevice?.Dispose();
        _d3dDevice = null;

        _hasFirstFrame = false;
        Interlocked.Exchange(ref _firstFrameTimestampTicks, 0);
        _lastWrittenFrameTimestampTicks = 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static GraphicsCaptureItem CreateCaptureItemForWindow(IntPtr windowHandle)
    {
        var interopId = GraphicsCaptureItemInteropGuid;
        var runtimeClassName = "Windows.Graphics.Capture.GraphicsCaptureItem";
        var createStringResult = WindowsCreateString(
            runtimeClassName,
            runtimeClassName.Length,
            out var runtimeClassId);
        Marshal.ThrowExceptionForHR(createStringResult);

        try
        {
            var result = RoGetActivationFactory(runtimeClassId, ref interopId, out var factoryPointer);
            Marshal.ThrowExceptionForHR(result);

            try
            {
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPointer);
            var itemId = GraphicsCaptureItemGuid;
            var createResult = interop.CreateForWindow(windowHandle, ref itemId, out var itemPointer);
            Marshal.ThrowExceptionForHR(createResult);
            if (itemPointer == IntPtr.Zero)
            {
                throw new InvalidOperationException("Could not create a graphics capture item for the window.");
            }

            try
            {
                return GraphicsCaptureItem.FromAbi(itemPointer);
            }
            finally
            {
                Marshal.Release(itemPointer);
            }
            }
            finally
            {
                Marshal.Release(factoryPointer);
            }
        }
        finally
        {
            WindowsDeleteString(runtimeClassId);
        }
    }

    private static IDirect3DDevice CreateDirect3DDevice(D3DDevice d3dDevice)
    {
        using var dxgiDevice = d3dDevice.QueryInterface<SharpDX.DXGI.Device3>();
        var result = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var graphicsDevice);
        Marshal.ThrowExceptionForHR(unchecked((int)result));

        try
        {
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevice);
        }
        finally
        {
        }
    }

    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid GraphicsCaptureItemInteropGuid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(
        IntPtr runtimeClassId,
        ref Guid iid,
        out IntPtr factory);

    [DllImport("d3d11.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern uint CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);

        int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr result);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }
}
