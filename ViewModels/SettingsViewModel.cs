using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using LandingRecorder.Models;
using LandingRecorder.Mvvm;

namespace LandingRecorder.ViewModels;

/// <summary>
/// Editable working copy of <see cref="AppSettings"/> for the Settings window. Nothing here touches the
/// live app: the caller reads <see cref="ToSettings"/> only when the dialog is confirmed with OK, so
/// Cancel discards edits simply by dropping this instance.
/// Numeric fields are bound as strings so invalid input can be reported instead of silently reverted.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private const string NoJoystickOption = "None";

    private readonly AppSettings _original;
    private readonly List<string?> _joystickValues = [];
    private readonly Func<string?, IReadOnlyList<JoystickAxis>> _getAvailableAxes;
    private int _selectedJoystickIndex;
    private bool _videoRecordingEnabled;
    private string _simProcessName;
    private string _autoStartHeightText;
    private string _autoStopDelayText;
    private string _recordingsFolder;
    private string? _errorText;
    private PlotLineEditorViewModel? _selectedPlotLine;

    /// <param name="getAvailableAxes">Returns the axes of the attached joystick with the given name (empty if none).</param>
    public SettingsViewModel(AppSettings current, IReadOnlyList<string> attachedJoystickNames, Func<string?, IReadOnlyList<JoystickAxis>> getAvailableAxes)
    {
        _original = current;
        _getAvailableAxes = getAvailableAxes;
        JoystickAxisOptions = Enum.GetValues<JoystickAxis>().Select(axis => new JoystickAxisOption(axis)).ToList();

        // JoystickOptions (display text) and _joystickValues (the JoystickName each maps to) are parallel lists.
        // "None" (null) always comes first. A saved device that isn't attached right now is kept as an
        // explicit "(not connected)" entry so pressing OK while it's unplugged doesn't silently reset it.
        var options = new List<string> { NoJoystickOption };
        _joystickValues.Add(null);
        foreach (var name in attachedJoystickNames)
        {
            options.Add(name);
            _joystickValues.Add(name);
        }

        if (current.JoystickName is not null && !attachedJoystickNames.Contains(current.JoystickName))
        {
            options.Add($"{current.JoystickName} (not connected)");
            _joystickValues.Add(current.JoystickName);
        }

        JoystickOptions = options;
        _selectedJoystickIndex = Math.Max(0, _joystickValues.IndexOf(current.JoystickName));
        UpdateAxisAvailability();

        _videoRecordingEnabled = current.VideoRecordingEnabled;
        _simProcessName = current.SimProcessName;
        _autoStartHeightText = Format(current.AutoStartHeightFeet);
        _autoStopDelayText = Format(current.AutoStopDelayAfterTouchdownSeconds);
        _recordingsFolder = current.GetRecordingsFolder();

        foreach (var line in current.PlotLines)
        {
            AddPlotLineEditor(new PlotLineEditorViewModel(line, JoystickAxisOptions));
        }

        _selectedPlotLine = PlotLines.FirstOrDefault();

        AddPlotLineCommand = new RelayCommand(() =>
        {
            var line = new PlotLineEditorViewModel(new PlotLineSettings
            {
                Name = $"Line {PlotLines.Count + 1}",
                ShortName = $"L{PlotLines.Count + 1}",
                Variables = [new PlotVariable()],
            }, JoystickAxisOptions);
            AddPlotLineEditor(line);
            SelectedPlotLine = line;
            Validate();
        });
        RemovePlotLineCommand = new RelayCommand(RemoveSelectedPlotLine, () => SelectedPlotLine is not null);
        MovePlotLineUpCommand = new RelayCommand(() => MoveSelectedPlotLine(-1), () => SelectedPlotLine is not null && PlotLines.IndexOf(SelectedPlotLine) > 0);
        MovePlotLineDownCommand = new RelayCommand(() => MoveSelectedPlotLine(1), () => SelectedPlotLine is not null && PlotLines.IndexOf(SelectedPlotLine) < PlotLines.Count - 1);

        Validate();
    }

    // --- Plot lines ---
    public ObservableCollection<PlotLineEditorViewModel> PlotLines { get; } = [];

    public PlotLineEditorViewModel? SelectedPlotLine
    {
        get => _selectedPlotLine;
        set
        {
            if (SetProperty(ref _selectedPlotLine, value))
            {
                OnPropertyChanged(nameof(HasSelectedPlotLine));
            }
        }
    }

    public bool HasSelectedPlotLine => SelectedPlotLine is not null;

    public ICommand AddPlotLineCommand { get; }

    public ICommand RemovePlotLineCommand { get; }

    public ICommand MovePlotLineUpCommand { get; }

    public ICommand MovePlotLineDownCommand { get; }

    public IReadOnlyList<string> JoystickOptions { get; }

    public int SelectedJoystickIndex
    {
        get => _selectedJoystickIndex;
        set
        {
            if (SetProperty(ref _selectedJoystickIndex, value))
            {
                UpdateAxisAvailability();
            }
        }
    }

    /// <summary>Shared, fixed list of all joystick axes for the variable rows' axis pickers.</summary>
    public IReadOnlyList<JoystickAxisOption> JoystickAxisOptions { get; }

    private string? SelectedJoystickName =>
        SelectedJoystickIndex >= 0 && SelectedJoystickIndex < _joystickValues.Count ? _joystickValues[SelectedJoystickIndex] : null;

    /// <summary>
    /// Marks the axes the dialog's selected joystick actually has. With None, or a device that isn't attached
    /// (so its axes can't be queried), every axis stays enabled rather than locking the picker.
    /// </summary>
    private void UpdateAxisAvailability()
    {
        var available = _getAvailableAxes(SelectedJoystickName);
        foreach (var option in JoystickAxisOptions)
        {
            option.IsAvailable = available.Count == 0 || available.Contains(option.Axis);
        }
    }

    public bool VideoRecordingEnabled
    {
        get => _videoRecordingEnabled;
        set { if (SetProperty(ref _videoRecordingEnabled, value)) Validate(); }
    }

    public string SimProcessName
    {
        get => _simProcessName;
        set { if (SetProperty(ref _simProcessName, value)) Validate(); }
    }

    public string AutoStartHeightText
    {
        get => _autoStartHeightText;
        set { if (SetProperty(ref _autoStartHeightText, value)) Validate(); }
    }

    public string AutoStopDelayText
    {
        get => _autoStopDelayText;
        set { if (SetProperty(ref _autoStopDelayText, value)) Validate(); }
    }

    /// <summary>Shown resolved (the executable's folder when unset); empty also means the executable's folder.</summary>
    public string RecordingsFolder
    {
        get => _recordingsFolder;
        set { if (SetProperty(ref _recordingsFolder, value)) Validate(); }
    }

    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                OnPropertyChanged(nameof(IsValid));
            }
        }
    }

    public bool IsValid => ErrorText is null;

    public AppSettings ToSettings()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException(ErrorText);
        }

        var joystickName = SelectedJoystickIndex >= 0 && SelectedJoystickIndex < _joystickValues.Count
            ? _joystickValues[SelectedJoystickIndex]
            : _original.JoystickName;

        return _original with
        {
            JoystickName = joystickName,
            VideoRecordingEnabled = VideoRecordingEnabled,
            SimProcessName = SimProcessName.Trim(),
            AutoStartHeightFeet = Parse(AutoStartHeightText),
            AutoStopDelayAfterTouchdownSeconds = Parse(AutoStopDelayText),
            RecordingsFolder = ToStoredRecordingsFolder(RecordingsFolder),
            PlotLines = PlotLines.Select(line => line.ToSettings()).ToList(),
        };
    }

    private void Validate()
    {
        ErrorText =
            VideoRecordingEnabled && string.IsNullOrWhiteSpace(SimProcessName)
                ? "MSFS process name is required when video recording is enabled."
            : !TryParse(AutoStartHeightText, out var height) || height <= 0
                ? "Start height must be a positive number of feet."
            : !TryParse(AutoStopDelayText, out var delay) || delay < 0
                ? "Stop delay must be zero or a positive number of seconds."
            : !string.IsNullOrWhiteSpace(RecordingsFolder) && !IsValidFolderPath(RecordingsFolder.Trim())
                ? "Recordings folder must be a full path, e.g. D:\\Recordings."
            : ValidatePlotLines();
    }

    private static bool IsValidFolderPath(string path) =>
        Path.IsPathFullyQualified(path) && path.IndexOfAny(Path.GetInvalidPathChars()) < 0;

    /// <summary>The executable's folder (or empty) is stored as unset, so it keeps following the app if it's moved.</summary>
    private static string? ToStoredRecordingsFolder(string folder)
    {
        folder = folder.Trim();
        return folder.Length == 0 || string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase)
            ? null
            : folder;
    }

    private string? ValidatePlotLines()
    {
        // Each simvar is requested from SimConnect (and recorded) once, so it can only have one units string.
        var unitsByName = new Dictionary<string, (string Units, string LineName)>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in PlotLines)
        {
            if (line.Validate() is string lineError)
            {
                return $"Plot line '{line.Name}': {lineError}";
            }

            foreach (var variable in line.Variables.Where(variable => variable.IsSimVar))
            {
                var name = variable.Name.Trim();
                var units = variable.Units.Trim();

                if (unitsByName.TryGetValue(name, out var existing) && !string.Equals(existing.Units, units, StringComparison.OrdinalIgnoreCase))
                {
                    return $"'{name}' is used with units '{existing.Units}' in '{existing.LineName}' and '{units}' in '{line.Name}'; use the same units.";
                }

                unitsByName[name] = (units, line.Name);
            }
        }

        return null;
    }

    private void AddPlotLineEditor(PlotLineEditorViewModel line)
    {
        line.Changed += (_, _) => Validate();
        PlotLines.Add(line);
    }

    private void RemoveSelectedPlotLine()
    {
        if (SelectedPlotLine is not { } line)
        {
            return;
        }

        var index = PlotLines.IndexOf(line);
        PlotLines.Remove(line);
        SelectedPlotLine = PlotLines.Count == 0 ? null : PlotLines[Math.Min(index, PlotLines.Count - 1)];
        Validate();
    }

    private void MoveSelectedPlotLine(int offset)
    {
        if (SelectedPlotLine is not { } line)
        {
            return;
        }

        var index = PlotLines.IndexOf(line);
        var newIndex = index + offset;
        if (newIndex < 0 || newIndex >= PlotLines.Count)
        {
            return;
        }

        PlotLines.Move(index, newIndex);
        SelectedPlotLine = line;
    }

    private static string Format(double value) => value.ToString(CultureInfo.CurrentCulture);

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value);

    private static double Parse(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.CurrentCulture);
}
