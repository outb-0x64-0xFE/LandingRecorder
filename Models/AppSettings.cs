namespace LandingRecorder.Models;

/// <summary>
/// User-configurable settings persisted to settings.json. Property initializers are the hardcoded
/// defaults, used both when settings.json doesn't exist and for any key missing from an existing file.
/// </summary>
public sealed record AppSettings
{
    /// <summary>
    /// Product name of the selected DirectInput device; null means None (joystick input isn't recorded).
    /// A saved device that isn't attached also behaves as None until it's plugged back in.
    /// </summary>
    public string? JoystickName { get; init; }

    public bool VideoRecordingEnabled { get; init; } = true;

    /// <summary>Matched against a process's name (exact, case-insensitive) or its main window title (substring).</summary>
    public string SimProcessName { get; init; } = "Microsoft Flight Simulator";

    /// <summary>Auto capture starts when height AGL descends through this value.</summary>
    public double AutoStartHeightFeet { get; init; } = 100;

    /// <summary>Auto capture stops this many seconds after touchdown.</summary>
    public double AutoStopDelayAfterTouchdownSeconds { get; init; } = 10;

    /// <summary>
    /// Folder recordings are auto-saved to (and the Load/Save dialogs open in); null or empty means the
    /// executable's folder. Kept unset by default rather than storing the exe path, so moving the app moves it too.
    /// </summary>
    public string? RecordingsFolder { get; init; }

    /// <summary><see cref="RecordingsFolder"/>, or the executable's folder when it isn't set.</summary>
    public string GetRecordingsFolder() =>
        string.IsNullOrWhiteSpace(RecordingsFolder) ? AppContext.BaseDirectory : RecordingsFolder;

    /// <summary>Plotted lines; the union of their variables is what gets requested from SimConnect and recorded.</summary>
    public IReadOnlyList<PlotLineSettings> PlotLines { get; init; } = DefaultPlotLines;

    public static IReadOnlyList<PlotLineSettings> DefaultPlotLines { get; } =
    [
        new()
        {
            // DirectInput's Y axis grows when the stick is pulled back; negated so pushing forward is positive.
            Name = "Joystick Y", ShortName = "JoyY", Color = "#FFB347", Formula = "-x1",
            Variables = [PlotVariable.Joystick(JoystickAxis.Y, AxisScale.Degrees, 25)],
            Min = -25, Max = 25, ScaleMode = ScrollScaleMode.Both, Unit = "°", Decimals = 1,
        },
        new()
        {
            // SimConnect's pitch is positive-nose-down; negated so positive means nose-up.
            Name = "Pitch attitude", ShortName = "Pitch", Color = "#00E5FF", Formula = "-x1",
            Variables = [PlotVariable.SimVar("PLANE PITCH DEGREES", "degrees")],
            Min = -20, Max = 20, ScaleMode = ScrollScaleMode.Both, Unit = "°", Decimals = 1,
        },
        new()
        {
            Name = "Elevator deflection", ShortName = "Elev", Color = "#47B3FF", Formula = "-x1",
            Variables = [PlotVariable.SimVar("ELEVATOR DEFLECTION", "degrees")],
            Min = -20, Max = 20, ScaleMode = ScrollScaleMode.Both, Unit = "°", Decimals = 1,
        },
        new()
        {
            Name = "Height AGL", ShortName = "AGL", Color = "#65DC91", Formula = "x1",
            Variables = [PlotVariable.SimVar("PLANE ALT ABOVE GROUND", "feet")],
            Min = 0, Max = 100, ScaleMode = ScrollScaleMode.MaxOnly, Unit = "ft", Decimals = 1,
        },
        new()
        {
            Name = "Vertical speed", ShortName = "VS", Color = "#FF57BB", Formula = "x1",
            Variables = [PlotVariable.SimVar("VELOCITY WORLD Y", "feet/minute")],
            Min = -2000, Max = 2000, ScaleMode = ScrollScaleMode.Both, Unit = "fpm", Decimals = 0,
        },
        new()
        {
            Name = "G-Force", ShortName = "G", Color = "#BA68FF", Formula = "x1",
            Variables = [PlotVariable.SimVar("G FORCE", "GForce")],
            Min = 0, Max = 2, ScaleMode = ScrollScaleMode.MaxOnly, Unit = "G", Decimals = 2,
        },
    ];

    /// <summary>
    /// Unique SimConnect simvars referenced by any plot line (joystick axes excluded). Names are
    /// case-insensitive; settings validation guarantees one units string per name.
    /// </summary>
    public IReadOnlyList<PlotVariable> GetRecordedSimVars()
    {
        var result = new List<PlotVariable>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in PlotLines.SelectMany(line => line.Variables))
        {
            if (!variable.IsJoystickAxis && seen.Add(variable.RecordKey))
            {
                result.Add(variable);
            }
        }

        return result;
    }

    /// <summary>Unique joystick axes referenced by any plot line; only these are read and recorded.</summary>
    public IReadOnlyList<JoystickAxis> GetRecordedJoystickAxes() =>
        PlotLines.SelectMany(line => line.Variables)
            .Where(variable => variable.IsJoystickAxis && variable.Axis is not null)
            .Select(variable => variable.Axis!.Value)
            .Distinct()
            .ToList();
}
