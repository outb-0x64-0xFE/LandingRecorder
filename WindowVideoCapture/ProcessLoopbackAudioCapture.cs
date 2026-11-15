using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace LandingRecorder;

/// <summary>
/// One captured WASAPI packet, already converted to 16-bit PCM, stamped with the same
/// QueryPerformanceCounter-based clock (100ns units) that WGC's <c>SystemRelativeTime</c> uses,
/// so it can be lined up with video frames without a separate sync mechanism.
/// </summary>
internal readonly record struct AudioChunk(byte[] Pcm16, int FrameCount, ulong QpcPosition100ns);

/// <summary>
/// Captures only the audio rendered by one process (and its child processes) via Windows'
/// process-specific WASAPI loopback (<c>AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK</c>, Windows
/// 10 2004+), modeled on Microsoft's published "ApplicationLoopback" sample. Not wrapped by
/// SharpDX or any referenced package, so the COM surface is declared here the same way
/// <see cref="WindowVideoRecorder"/> already declares its own WinRT/WGC interop.
/// </summary>
internal sealed class ProcessLoopbackAudioCapture : IDisposable, ProcessLoopbackAudioCapture.IActivateAudioInterfaceCompletionHandler
{
    private const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";
    private const int AudioClientActivationTypeProcessLoopback = 1;
    private const int ProcessLoopbackModeIncludeTargetProcessTree = 0;
    private const int ShareModeShared = 0;
    private const uint StreamFlagsLoopback = 0x00020000;
    private const uint StreamFlagsEventCallback = 0x00040000;
    private const uint BufferFlagsSilent = 0x2;
    private const long BufferDuration100ns = 2_000_000; // 200ms
    private const uint WaitObject0 = 0;

    // IAudioClient/IAudioCaptureClient vtable slot indices (0-based, after IUnknown's own
    // QueryInterface/AddRef/Release in slots 0-2), per audioclient.h's declared method order.
    private const int AudioClientVtblInitialize = 3;
    private const int AudioClientVtblStart = 10;
    private const int AudioClientVtblStop = 11;
    private const int AudioClientVtblSetEventHandle = 13;
    private const int AudioClientVtblGetService = 14;
    private const int AudioCaptureClientVtblGetBuffer = 3;
    private const int AudioCaptureClientVtblReleaseBuffer = 4;
    private const int AudioCaptureClientVtblGetNextPacketSize = 5;

    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    private readonly Channel<AudioChunk> _channel = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(64)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = true
    });

    private TaskCompletionSource<bool>? _activationCompletion;

    // IAudioClient/IAudioCaptureClient are called as raw COM vtable pointers (via
    // GetVtableMethod below), never wrapped as managed RCW/interface objects. Wrapping them the
    // normal way - a managed cast, or Marshal.GetTypedObjectForIUnknown - was observed to fail
    // reproducibly with E_NOINTERFACE against a real target process (while working fine against
    // a synthetic self-hosted test process), even though an explicit native QueryInterface for
    // the exact same IID on the exact same pointer succeeds. Whatever that gap is, it lives
    // specifically in .NET's own typed-RCW creation for this async-activation object in this
    // environment, so the fix is to never ask .NET to create one.
    private IntPtr _audioClient;
    private IntPtr _captureClient;
    private IntPtr _eventHandle;
    private Thread? _captureThread;
    private volatile bool _running;
    private bool _disposed;

    public ChannelReader<AudioChunk> Reader => _channel.Reader;

    public int SampleRate { get; private set; }

    public int Channels { get; private set; }

    public void Start(int targetProcessId)
    {
        _activationCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var activationParams = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = AudioClientActivationTypeProcessLoopback,
            TargetProcessId = (uint)targetProcessId,
            ProcessLoopbackMode = ProcessLoopbackModeIncludeTargetProcessTree
        };

        var paramsPointer = Marshal.AllocHGlobal(Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>());
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPointer, false);

            var propvariant = new PROPVARIANT
            {
                vt = 0x41, // VT_BLOB
                cbSize = (uint)Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>(),
                pBlobData = paramsPointer
            };

            var audioClientIid = IidAudioClient;
            var hr = ActivateAudioInterfaceAsync(
                VirtualAudioDeviceProcessLoopback,
                ref audioClientIid,
                ref propvariant,
                this,
                out _);
            Marshal.ThrowExceptionForHR(hr);
        }
        finally
        {
            // Safe to free immediately: the audio subsystem reads the activation params
            // synchronously during the call above, before this method returns - the same
            // lifetime the reference "ApplicationLoopback" sample relies on by using a stack
            // variable here.
            Marshal.FreeHGlobal(paramsPointer);
        }

        if (!_activationCompletion.Task.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("Timed out activating process-specific loopback audio.");
        }

        _ = _activationCompletion.Task.GetAwaiter().GetResult();

        // GetMixFormat is documented as unsupported (returns E_NOTIMPL) on an IAudioClient
        // activated via process-loopback - there's no real device backing it to query. The
        // format must be supplied up front instead; 2-channel 48kHz 32-bit float is the
        // standard choice (matches the WASAPI engine's own typical mix format).
        const ushort WaveFormatIeeeFloat = 3;
        const int SourceChannels = 2;
        const int SourceSampleRate = 48000;
        const int SourceBitsPerSample = 32;
        var blockAlign = (ushort)(SourceChannels * SourceBitsPerSample / 8);

        SampleRate = SourceSampleRate;
        Channels = SourceChannels;
        _sourceBitsPerSample = SourceBitsPerSample;

        var format = new WAVEFORMATEX
        {
            FormatTag = WaveFormatIeeeFloat,
            Channels = SourceChannels,
            SamplesPerSec = SourceSampleRate,
            AvgBytesPerSec = (uint)(SourceSampleRate * blockAlign),
            BlockAlign = blockAlign,
            BitsPerSample = SourceBitsPerSample,
            Size = 0
        };

        var formatPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEX>());
        try
        {
            Marshal.StructureToPtr(format, formatPointer, false);
            var initializeFn = GetVtableMethod<AudioClientInitializeFn>(_audioClient, AudioClientVtblInitialize);
            Marshal.ThrowExceptionForHR(initializeFn(
                _audioClient,
                ShareModeShared,
                StreamFlagsLoopback | StreamFlagsEventCallback,
                BufferDuration100ns,
                0,
                formatPointer,
                IntPtr.Zero));
        }
        finally
        {
            Marshal.FreeHGlobal(formatPointer);
        }

        _eventHandle = CreateEventEx(IntPtr.Zero, null, 0, 0x1F0003);
        if (_eventHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Could not create the audio capture event.");
        }
        var setEventHandleFn = GetVtableMethod<AudioClientSetEventHandleFn>(_audioClient, AudioClientVtblSetEventHandle);
        Marshal.ThrowExceptionForHR(setEventHandleFn(_audioClient, _eventHandle));

        var getServiceFn = GetVtableMethod<AudioClientGetServiceFn>(_audioClient, AudioClientVtblGetService);
        var captureClientIid = IidAudioCaptureClient;
        Marshal.ThrowExceptionForHR(getServiceFn(_audioClient, ref captureClientIid, out var captureClientPointer));
        _captureClient = captureClientPointer;

        var startFn = GetVtableMethod<AudioClientStartStopFn>(_audioClient, AudioClientVtblStart);
        Marshal.ThrowExceptionForHR(startFn(_audioClient));
        _running = true;
        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "ProcessLoopbackAudioCapture" };
        _captureThread.Start();
    }

    private int _sourceBitsPerSample;

    private void CaptureLoop()
    {
        try
        {
            var getNextPacketSizeFn = GetVtableMethod<AudioCaptureClientGetNextPacketSizeFn>(_captureClient, AudioCaptureClientVtblGetNextPacketSize);
            var getBufferFn = GetVtableMethod<AudioCaptureClientGetBufferFn>(_captureClient, AudioCaptureClientVtblGetBuffer);
            var releaseBufferFn = GetVtableMethod<AudioCaptureClientReleaseBufferFn>(_captureClient, AudioCaptureClientVtblReleaseBuffer);

            while (_running)
            {
                var waitResult = WaitForSingleObject(_eventHandle, 2000);
                if (!_running)
                {
                    break;
                }
                if (waitResult != WaitObject0)
                {
                    continue;
                }

                Marshal.ThrowExceptionForHR(getNextPacketSizeFn(_captureClient, out var packetFrames));
                while (packetFrames != 0)
                {
                    Marshal.ThrowExceptionForHR(getBufferFn(_captureClient, out var dataPointer, out var framesToRead, out var bufferFlags, out _, out var qpcPosition));

                    var pcm16 = ConvertToPcm16(dataPointer, (int)framesToRead, (bufferFlags & BufferFlagsSilent) != 0);
                    _channel.Writer.TryWrite(new AudioChunk(pcm16, (int)framesToRead, qpcPosition));

                    Marshal.ThrowExceptionForHR(releaseBufferFn(_captureClient, framesToRead));
                    Marshal.ThrowExceptionForHR(getNextPacketSizeFn(_captureClient, out packetFrames));
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.WriteLine($"Process-specific audio capture loop stopped unexpectedly: {ex.Message}");
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    private byte[] ConvertToPcm16(IntPtr dataPointer, int frameCount, bool silent)
    {
        var sampleCount = frameCount * Channels;
        var pcm16 = new byte[sampleCount * 2];

        if (silent)
        {
            return pcm16;
        }

        if (_sourceBitsPerSample == 16)
        {
            Marshal.Copy(dataPointer, pcm16, 0, pcm16.Length);
            return pcm16;
        }

        var floatSamples = new float[sampleCount];
        Marshal.Copy(dataPointer, floatSamples, 0, sampleCount);
        for (var i = 0; i < sampleCount; i++)
        {
            var clamped = Math.Clamp(floatSamples[i], -1f, 1f);
            var sample16 = (short)(clamped * short.MaxValue);
            pcm16[i * 2] = (byte)(sample16 & 0xFF);
            pcm16[i * 2 + 1] = (byte)((sample16 >> 8) & 0xFF);
        }
        return pcm16;
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        if (_eventHandle != IntPtr.Zero)
        {
            SetEvent(_eventHandle);
        }
        _captureThread?.Join();
        _captureThread = null;

        try
        {
            if (_audioClient != IntPtr.Zero)
            {
                var stopFn = GetVtableMethod<AudioClientStartStopFn>(_audioClient, AudioClientVtblStop);
                stopFn(_audioClient);
            }
        }
        catch (Exception ex)
        {
            AppLogger.WriteLine($"Error stopping process-specific audio capture: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();

        if (_captureClient != IntPtr.Zero)
        {
            Marshal.Release(_captureClient);
            _captureClient = IntPtr.Zero;
        }
        if (_audioClient != IntPtr.Zero)
        {
            Marshal.Release(_audioClient);
            _audioClient = IntPtr.Zero;
        }
        if (_eventHandle != IntPtr.Zero)
        {
            CloseHandle(_eventHandle);
            _eventHandle = IntPtr.Zero;
        }

        _disposed = true;
    }

    void IActivateAudioInterfaceCompletionHandler.ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
    {
        try
        {
            activateOperation.GetActivateResult(out var activateResult, out var activatedInterfacePointer);
            Marshal.ThrowExceptionForHR(activateResult);

            // Keep the QueryInterface result as a raw pointer - see the field comment on
            // _audioClient for why it's never wrapped as a managed RCW/interface object.
            try
            {
                var audioClientIid = IidAudioClient;
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(activatedInterfacePointer, ref audioClientIid, out var audioClientPointer));
                _audioClient = audioClientPointer;
            }
            finally
            {
                Marshal.Release(activatedInterfacePointer);
            }

            _activationCompletion!.TrySetResult(true);
        }
        catch (Exception ex)
        {
            _activationCompletion!.TrySetException(ex);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public uint cbSize;
        public IntPtr pBlobData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, out IntPtr activatedInterface);
    }

    // IAudioClient/IAudioCaptureClient are never declared as [ComImport] interfaces - they're
    // called through raw vtable function pointers instead (see the _audioClient field comment).
    // Each delegate's native signature: stdcall, HRESULT return, `this` as the first parameter.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AudioClientInitializeFn(IntPtr self, int shareMode, uint streamFlags, long bufferDuration100ns, long periodicity100ns, IntPtr format, IntPtr audioSessionGuid);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AudioClientSetEventHandleFn(IntPtr self, IntPtr eventHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AudioClientGetServiceFn(IntPtr self, ref Guid riid, out IntPtr service);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AudioClientStartStopFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AudioCaptureClientGetBufferFn(IntPtr self, out IntPtr dataBuffer, out uint numFramesToRead, out uint bufferFlags, out ulong devicePosition, out ulong qpcPosition);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AudioCaptureClientReleaseBufferFn(IntPtr self, uint numFramesRead);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AudioCaptureClientGetNextPacketSizeFn(IntPtr self, out uint numFramesInNextPacket);

    private static TDelegate GetVtableMethod<TDelegate>(IntPtr comObject, int slotIndex) where TDelegate : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        var functionPointer = Marshal.ReadIntPtr(vtable, slotIndex * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(functionPointer);
    }

    [DllImport("mmdevapi.dll", PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        ref PROPVARIANT activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEventEx(IntPtr lpEventAttributes, string? lpName, uint dwFlags, uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
