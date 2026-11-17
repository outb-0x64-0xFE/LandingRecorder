using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using LandingRecorder.Models;
using LandingRecorder.Mvvm;
using LandingRecorder.Services;

namespace LandingRecorder.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxPoints = 10000;

    private static readonly TimeSpan ConnectedTickInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan DisconnectedTickInterval = TimeSpan.FromSeconds(5);

    private readonly SimConnectService _simConnectService;
    private readonly JoystickInputService _joystickInput;
    private readonly VideoCaptureService _videoCaptureService;
    private readonly SettingsService _settingsService;
    private readonly DispatcherTimer _timer;
    private readonly List<DataPoint> _points = [];
    private readonly RelayCommand _playVideoCommand;
    private readonly RelayCommand _pauseVideoCommand;

    private AppSettings _settings = new();
    private IReadOnlyList<JoystickAxis> _recordedJoystickAxes = [];
    private bool _isRecording;
    private bool _autoCaptureEnabled = false;
    private DateTime _startTime;

    private bool _touchdownVisible = true;

    // Variables (name -> units) present in the displayed data: the loaded file's header, or what the
    // current/last live recording captured. Null when there's no data. Drives each PlotLine's availability.
    private IReadOnlyDictionary<string, string>? _dataVariables;

    private double _previousHeightAboveGroundFeet = double.NaN;
    private bool? _previousSimOnGround;
    private DateTime? _groundContactTimeUtc;
    private string? _videoOutputPath;
    private string? _recordingDataPath;
    private Uri? _videoSource;
    private bool _isVideoPlaying;

    public MainViewModel(SimConnectService simConnectService, JoystickInputService joystickInputService, VideoCaptureService videoCaptureService, SettingsService settingsService)
    {
        _simConnectService = simConnectService;
        _joystickInput = joystickInputService;
        _videoCaptureService = videoCaptureService;
        _settingsService = settingsService;

        _simConnectService.ConnectionStateChanged += SimConnectService_ConnectionStateChanged;

        _timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher.CurrentDispatcher)
        {
            Interval = DisconnectedTickInterval
        };
        _timer.Tick += Tick;

        ToggleRecordingCommand = new RelayCommand(() =>
        {
            if (IsRecording)
            {
                StopRecording();
            }
            else
            {
                StartRecording();
            }
        });
        ResetCommand = new RelayCommand(Reset);
        _playVideoCommand = new RelayCommand(() => SetIsVideoPlaying(true), () => CanPlayVideo);
        _pauseVideoCommand = new RelayCommand(() => SetIsVideoPlaying(false), () => CanPauseVideo);
        PlayVideoCommand = _playVideoCommand;
        PauseVideoCommand = _pauseVideoCommand;
    }

    // --- Recording / status ---
    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (SetProperty(ref _isRecording, value))
            {
                OnPropertyChanged(nameof(RecordButtonLabel), nameof(StatusText), nameof(CanSave), nameof(CanLoad), nameof(CanOpenSettings));
            }
        }
    }

    public bool AutoCaptureEnabled
    {
        get => _autoCaptureEnabled;
        set => SetProperty(ref _autoCaptureEnabled, value);
    }

    public string AutoCaptureLabel =>
        $"Auto ({_settings.AutoStartHeightFeet:0.#}ft / +{_settings.AutoStopDelayAfterTouchdownSeconds:0.#}s)";

    public string RecordButtonLabel => IsRecording ? "Stop recording" : "Start recording";

    public string StatusText => IsRecording ? "Recording" : "Idle";

    public bool IsSimConnected => _simConnectService.IsConnected;

    public string SimConnectStatusText => IsSimConnected ? "SimConnect: connected" : "SimConnect: not connected";

    public bool CanSave => !IsRecording && _points.Count > 0;

    public bool CanLoad => !IsRecording;

    public bool CanOpenSettings => !IsRecording;

    /// <summary>Folder the Load/Save dialogs open in: the configured recordings folder, if it exists.</summary>
    public string? RecordingsFolderForDialogs =>
        Directory.Exists(_settings.GetRecordingsFolder()) ? _settings.GetRecordingsFolder() : null;

    // --- Graph data (read by View's DrawGraph) ---
    public IReadOnlyList<DataPoint> Points => _points;

    public int? SelectedPointIndex { get; private set; }

    /// <summary>Configured plot lines (rebuilt whenever settings are applied), drawn and listed in the legend.</summary>
    public ObservableCollection<PlotLineViewModel> PlotLines { get; } = [];

    public bool TouchdownVisible
    {
        get => _touchdownVisible;
        set
        {
            if (SetProperty(ref _touchdownVisible, value))
            {
                GraphInvalidated?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? GraphInvalidated;

    // --- Video ---
    public Uri? VideoSource => _videoSource;

    public bool IsVideoPlaying => _isVideoPlaying;

    public bool CanPlayVideo => VideoSource is not null && !IsVideoPlaying;

    public bool CanPauseVideo => IsVideoPlaying;

    // --- Commands ---
    public ICommand ToggleRecordingCommand { get; }

    public ICommand ResetCommand { get; }

    public ICommand PlayVideoCommand { get; }

    public ICommand PauseVideoCommand { get; }

    public void Initialize(IntPtr ownerWindowHandle)
    {
        ApplySettings(_settingsService.Load(), refreshJoysticks: true);
        _simConnectService.Connect(ownerWindowHandle);
        _timer.Start();
        GraphInvalidated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Builds an editable copy of the current settings for the Settings window. Re-enumerates joysticks
    /// first (keeping the active one selected) so devices plugged in since startup show up.
    /// </summary>
    public SettingsViewModel CreateSettingsEditor()
    {
        _joystickInput.RefreshDevices(_settings.JoystickName);
        return new SettingsViewModel(_settings, [.. _joystickInput.DeviceNames], _joystickInput.GetAvailableAxes);
    }

    /// <summary>Applies confirmed settings to the running app and persists them to settings.json.</summary>
    public void ApplyAndSaveSettings(AppSettings settings)
    {
        if (IsRecording)
        {
            throw new InvalidOperationException("Settings can't be changed while recording.");
        }

        ApplySettings(settings, refreshJoysticks: false);
        _settingsService.Save(settings);
    }

    public void SavePoints(string filePath) => CreateRecording().Save(filePath);

    public void LoadPoints(string filePath)
    {
        var recording = Recording.Load(filePath);

        _points.Clear();
        _points.AddRange(recording.Points);
        SelectedPointIndex = null;

        // A variable counts as present only if the header lists it AND some point actually has a value
        // for it (a simvar SimConnect didn't recognize is in the header but never delivered data).
        SetDataVariables(recording.Variables
            .Where(variable => recording.Points.Any(point => point.Values.ContainsKey(variable.Key)))
            .ToDictionary(variable => variable.Key, variable => variable.Value, StringComparer.OrdinalIgnoreCase));

        var videoPath = Path.ChangeExtension(filePath, ".mp4");
        if (File.Exists(videoPath))
        {
            OpenVideo(videoPath);
        }
        else
        {
            SetVideoSource(null);
            SetIsVideoPlaying(false);
        }

        OnPropertyChanged(nameof(CanSave));
        GraphInvalidated?.Invoke(this, EventArgs.Empty);
    }

    public void SelectNearestPointByTime(double timeSeconds)
    {
        SelectedPointIndex = _points.Count == 0
            ? null
            : _points
                .Select((point, index) => (point, index))
                .MinBy(item => Math.Abs(item.point.TimeSeconds - timeSeconds))
                .index;

        GraphInvalidated?.Invoke(this, EventArgs.Empty);
    }

    public void ClearSelection()
    {
        SelectedPointIndex = null;
        GraphInvalidated?.Invoke(this, EventArgs.Empty);
    }

    public void ZoomPlotLine(PlotLineViewModel line, int wheelDelta)
    {
        line.Zoom(wheelDelta);
        GraphInvalidated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Zooms keeping <paramref name="anchorValue"/> fixed on screen (scroll over a line's graph handle).</summary>
    public void ZoomPlotLineAround(PlotLineViewModel line, int wheelDelta, double anchorValue)
    {
        line.ZoomAround(wheelDelta, anchorValue);
        GraphInvalidated?.Invoke(this, EventArgs.Empty);
    }

    public void ResetPlotLineRange(PlotLineViewModel line)
    {
        line.ResetRange();
        GraphInvalidated?.Invoke(this, EventArgs.Empty);
    }

    public void SetPlotLineRange(PlotLineViewModel line, double min, double max)
    {
        line.SetRange(min, max);
        GraphInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private Recording CreateRecording() =>
        new(_dataVariables ?? new Dictionary<string, string>(), [.. _points]);

    private void SetDataVariables(IReadOnlyDictionary<string, string>? variables)
    {
        _dataVariables = variables;
        foreach (var line in PlotLines)
        {
            line.UpdateAvailability(variables);
        }
    }

    private void RebuildPlotLines(IReadOnlyList<PlotLineSettings> lineSettings)
    {
        // Keep each line's visibility checkbox state across a settings change when its name is unchanged.
        var previousVisibility = PlotLines
            .GroupBy(line => line.Name)
            .ToDictionary(group => group.Key, group => group.First().IsVisible);

        foreach (var line in PlotLines)
        {
            line.PropertyChanged -= PlotLine_PropertyChanged;
        }

        PlotLines.Clear();
        foreach (var settings in lineSettings)
        {
            var line = new PlotLineViewModel(settings)
            {
                IsVisible = previousVisibility.GetValueOrDefault(settings.Name, true),
            };
            line.UpdateAvailability(_dataVariables);
            line.PropertyChanged += PlotLine_PropertyChanged;
            PlotLines.Add(line);
        }

        GraphInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void PlotLine_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlotLineViewModel.IsVisible))
        {
            GraphInvalidated?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ApplySettings(AppSettings settings, bool refreshJoysticks)
    {
        _settings = settings;

        if (refreshJoysticks)
        {
            _joystickInput.RefreshDevices(settings.JoystickName);
        }
        else
        {
            _joystickInput.SelectDevice(settings.JoystickName);
        }

        _videoCaptureService.SimProcessName = settings.SimProcessName;
        _simConnectService.SetRecordedVariables(settings.GetRecordedSimVars());
        _recordedJoystickAxes = settings.GetRecordedJoystickAxes();
        RebuildPlotLines(settings.PlotLines);

        if (settings.VideoRecordingEnabled && _simConnectService.IsConnected)
        {
            _videoCaptureService.EnsureInitialized();
        }

        OnPropertyChanged(nameof(AutoCaptureLabel));
    }

    private void SimConnectService_ConnectionStateChanged(object? sender, bool isConnected)
    {
        OnPropertyChanged(nameof(IsSimConnected), nameof(SimConnectStatusText));
        _timer.Interval = isConnected ? ConnectedTickInterval : DisconnectedTickInterval;
        if (isConnected && _settings.VideoRecordingEnabled)
        {
            _videoCaptureService.EnsureInitialized();
        }
    }

    private void StartRecording()
    {
        IsRecording = true;
        _points.Clear();
        SelectedPointIndex = null;
        _groundContactTimeUtc = null;
        EnsureJoystickForRecording();
        SetDataVariables(GetLiveRecordingVariables());
        var recordingsFolder = EnsureRecordingsFolder();
        StartVideoCapture(recordingsFolder);
        // The data JSON is auto-saved on stop: as a sidecar next to the .mp4 when video is being
        // captured, otherwise (video disabled in settings, or capture failed to start) on its own.
        _recordingDataPath = _videoOutputPath is not null
            ? Path.ChangeExtension(_videoOutputPath, ".json")
            : VideoCaptureService.CreateOutputPath(recordingsFolder, ".json");
        _startTime = DateTime.UtcNow;
        OnPropertyChanged(nameof(CanSave));
        GraphInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void StopRecording()
    {
        IsRecording = false;
        // Disarm the auto-stop timer; otherwise it stays expired and StopRecording() re-runs on every idle tick.
        _groundContactTimeUtc = null;
        StopVideoCapture();

        if (_recordingDataPath is not null && _points.Count > 0)
        {
            SaveRecordingFile(_recordingDataPath);
        }

        _recordingDataPath = null;
    }

    private void Reset()
    {
        _points.Clear();
        SelectedPointIndex = null;
        SetDataVariables(null);
        SetVideoSource(null);
        SetIsVideoPlaying(false);
        OnPropertyChanged(nameof(CanSave));
        GraphInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void Tick(object? sender, EventArgs e)
    {
        _simConnectService.Pump();
        var heightAboveGroundFeet = _simConnectService.HeightAboveGroundFeet;
        var simOnGround = _simConnectService.SimOnGround;

        var isTouchdownSim = _previousSimOnGround == false && simOnGround;

        if (AutoCaptureEnabled)
        {
            if (!IsRecording)
            {
                if (!double.IsNaN(_previousHeightAboveGroundFeet) && _previousHeightAboveGroundFeet > _settings.AutoStartHeightFeet
                    && heightAboveGroundFeet <= _settings.AutoStartHeightFeet)
                {
                    StartRecording();
                }
            }
            else if (isTouchdownSim)
            {
                _groundContactTimeUtc ??= DateTime.UtcNow;
            }

            if (_groundContactTimeUtc is not null
                && DateTime.UtcNow - _groundContactTimeUtc.Value >= TimeSpan.FromSeconds(_settings.AutoStopDelayAfterTouchdownSeconds))
            {
                StopRecording();
            }
        }

        if (IsRecording)
        {
            var elapsedSeconds = (DateTime.UtcNow - _startTime).TotalSeconds;
            var values = _simConnectService.GetRecordedValuesSnapshot();
            _joystickInput.ReadAxes(_recordedJoystickAxes, values);

            // TouchdownSim is always recorded, independent of which variables the plot lines use.
            _points.Add(new DataPoint(elapsedSeconds, values, isTouchdownSim));
        }

        _previousHeightAboveGroundFeet = heightAboveGroundFeet;
        _previousSimOnGround = simOnGround;

        if (_points.Count > MaxPoints)
        {
            _points.RemoveAt(1);
        }

        OnPropertyChanged(nameof(CanSave));
        GraphInvalidated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Re-checks the configured joystick right before a recording starts: re-enumerating picks the device
    /// back up if it was re-plugged since startup, and if it isn't attached nothing is selected, so no joystick
    /// axes are recorded for the whole recording instead of stale/zero values from a lost device.
    /// </summary>
    private void EnsureJoystickForRecording()
    {
        if (_settings.JoystickName is null)
        {
            return;
        }

        _joystickInput.RefreshDevices(_settings.JoystickName);
        if (_joystickInput.SelectedDeviceName is null)
        {
            AppLogger.WriteLine($"Joystick '{_settings.JoystickName}' not present at recording start; joystick axes will not be recorded.");
        }
    }

    /// <summary>Header for a live recording: the configured simvars, plus the used joystick axes if a joystick is present.</summary>
    private Dictionary<string, string> GetLiveRecordingVariables()
    {
        var variables = _settings.GetRecordedSimVars()
            .ToDictionary(variable => variable.RecordKey, variable => variable.Units ?? "", StringComparer.OrdinalIgnoreCase);
        if (_joystickInput.SelectedDeviceName is not null)
        {
            foreach (var axis in _recordedJoystickAxes)
            {
                variables[PlotVariable.JoystickRecordKey(axis)] = PlotVariable.JoystickUnits;
            }
        }

        return variables;
    }

    private void SaveRecordingFile(string filePath)
    {
        try
        {
            CreateRecording().Save(filePath);
            AppLogger.WriteLine($"Recording data saved: {filePath}");
        }
        catch (Exception ex)
        {
            AppLogger.WriteLine($"Failed to save recording data: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates the configured recordings folder if needed. If that fails (e.g. a removed drive), falls back to
    /// the executable's folder so the recording isn't lost.
    /// </summary>
    private string EnsureRecordingsFolder()
    {
        var folder = _settings.GetRecordingsFolder();
        try
        {
            Directory.CreateDirectory(folder);
            return folder;
        }
        catch (Exception ex)
        {
            AppLogger.WriteLine($"Recordings folder '{folder}' is unavailable ({ex.Message}); saving next to the executable instead.");
            return AppContext.BaseDirectory;
        }
    }

    private void StartVideoCapture(string recordingsFolder)
    {
        // Never carry a previous recording's path over: StartRecording derives the data JSON path from it,
        // so a stale value would make a recording whose video failed overwrite the previous recording's JSON.
        _videoOutputPath = null;

        // Retry the window lookup here too, not just on connect: MSFS may have opened its window after
        // SimConnect connected, or the process name may have just been changed in Settings.
        if (!_settings.VideoRecordingEnabled || !_videoCaptureService.EnsureInitialized())
        {
            return;
        }

        var outputPath = _videoCaptureService.Start(recordingsFolder, 60);
        if (outputPath is not null)
        {
            _videoOutputPath = outputPath;
        }
    }

    private void StopVideoCapture()
    {
        if (!_videoCaptureService.IsRecording)
        {
            return;
        }

        _videoCaptureService.Stop();
        if (_videoOutputPath is not null)
        {
            OpenVideo(_videoOutputPath);
            _videoOutputPath = null;
        }
    }

    private void OpenVideo(string videoPath)
    {
        if (!File.Exists(videoPath))
        {
            return;
        }

        try
        {
            SetVideoSource(new Uri(videoPath, UriKind.Absolute));
            SelectNearestPointByTime(0);
            SetIsVideoPlaying(false);
        }
        catch (Exception ex)
        {
            SetVideoSource(null);
            SetIsVideoPlaying(false);
            AppLogger.WriteLine($"Failed to open captured video: {ex.Message}");
        }
    }

    private void SetVideoSource(Uri? value)
    {
        if (SetProperty(ref _videoSource, value, nameof(VideoSource)))
        {
            NotifyVideoCommandsCanExecuteChanged();
        }
    }

    private void SetIsVideoPlaying(bool value)
    {
        if (SetProperty(ref _isVideoPlaying, value, nameof(IsVideoPlaying)))
        {
            NotifyVideoCommandsCanExecuteChanged();
        }
    }

    private void NotifyVideoCommandsCanExecuteChanged()
    {
        OnPropertyChanged(nameof(CanPlayVideo), nameof(CanPauseVideo));
        _playVideoCommand.RaiseCanExecuteChanged();
        _pauseVideoCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        _timer.Stop();
        if (IsRecording)
        {
            StopRecording();
        }

        _videoCaptureService.Dispose();
        _joystickInput.Dispose();
        _simConnectService.Dispose();
    }
}
