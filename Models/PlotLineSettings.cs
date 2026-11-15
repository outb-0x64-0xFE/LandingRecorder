using System.Text.Json.Serialization;

namespace LandingRecorder.Models;

/// <summary>What a mouse-wheel zoom over a plot line's range changes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScrollScaleMode>))]
public enum ScrollScaleMode
{
    /// <summary>Both bounds move symmetrically around the range's center.</summary>
    Both,

    /// <summary>Only the lower bound moves; the upper bound stays fixed.</summary>
    MinOnly,

    /// <summary>Only the upper bound moves; the lower bound stays fixed.</summary>
    MaxOnly,
}

/// <summary>Where a plot variable's value comes from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VariableSource>))]
public enum VariableSource
{
    /// <summary>A SimConnect simulation variable, requested by name in the given units.</summary>
    SimVar,

    /// <summary>An axis of the joystick selected in settings, scaled to ±max deflection degrees.</summary>
    JoystickAxis,
}

/// <summary>How a joystick axis value is presented to the plot line's formula.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AxisScale>))]
public enum AxisScale
{
    /// <summary>Percent of full deflection, -100..100.</summary>
    Percent,

    /// <summary>Degrees, -max..max where max is the variable's <see cref="PlotVariable.MaxDeflectionDeg"/>.</summary>
    Degrees,
}

/// <summary>The standard DirectInput joystick axes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JoystickAxis>))]
public enum JoystickAxis
{
    X,
    Y,
    Z,
    RotationX,
    RotationY,
    RotationZ,
    Slider1,
    Slider2,
}

/// <summary>
/// One input of a plot line (x1..xN in its formula): either a SimConnect simvar (<see cref="Name"/> +
/// <see cref="Units"/>) or a joystick axis (<see cref="Axis"/>, shown as percent or as degrees with its own
/// max deflection). Only the fields of the active source are set/serialized.
/// </summary>
public sealed record PlotVariable
{
    public VariableSource Source { get; init; } = VariableSource.SimVar;

    /// <summary>Simvar name (SimVar source only).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    /// <summary>SimConnect units the simvar is requested and recorded in (SimVar source only).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Units { get; init; }

    /// <summary>Joystick axis (JoystickAxis source only).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JoystickAxis? Axis { get; init; }

    /// <summary>Display scale of the axis (JoystickAxis source only); null means percent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AxisScale? Scale { get; init; }

    /// <summary>Full deflection in degrees when <see cref="Scale"/> is Degrees (JoystickAxis source only).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MaxDeflectionDeg { get; init; }

    [JsonIgnore]
    public bool IsJoystickAxis => Source == VariableSource.JoystickAxis;

    /// <summary>Key of this variable's value in recorded points: the simvar name, or e.g. "JoyY" for an axis.</summary>
    [JsonIgnore]
    public string RecordKey => IsJoystickAxis ? JoystickRecordKey(Axis ?? JoystickAxis.Y) : Name ?? "";

    /// <summary>Human-readable name for messages, e.g. "PLANE PITCH DEGREES" or "joystick Y".</summary>
    [JsonIgnore]
    public string DisplayName => IsJoystickAxis ? $"joystick {AxisDisplayName(Axis ?? JoystickAxis.Y)}" : Name ?? "";

    /// <summary>
    /// Units of joystick axes in recordings: always percent of full deflection (-100..100), independent of
    /// the display scale, so changing an axis's max deflection later still displays old recordings correctly.
    /// </summary>
    public const string JoystickUnits = "percent";

    public const double DefaultMaxDeflectionDeg = 25;

    /// <summary>
    /// Multiplier from the recorded value to what the formula sees: 1 for simvars and percent axes,
    /// maxDeflection/100 for degree-scaled axes.
    /// </summary>
    [JsonIgnore]
    public double DisplayFactor => IsJoystickAxis && Scale == AxisScale.Degrees
        ? (MaxDeflectionDeg ?? DefaultMaxDeflectionDeg) / 100.0
        : 1.0;

    public static PlotVariable SimVar(string name, string units) => new() { Source = VariableSource.SimVar, Name = name, Units = units };

    public static PlotVariable Joystick(JoystickAxis axis, AxisScale scale = AxisScale.Percent, double? maxDeflectionDeg = null) => new()
    {
        Source = VariableSource.JoystickAxis,
        Axis = axis,
        Scale = scale,
        MaxDeflectionDeg = scale == AxisScale.Degrees ? maxDeflectionDeg ?? DefaultMaxDeflectionDeg : null,
    };

    public static string JoystickRecordKey(JoystickAxis axis) => axis switch
    {
        JoystickAxis.X => "JoyX",
        JoystickAxis.Y => "JoyY",
        JoystickAxis.Z => "JoyZ",
        JoystickAxis.RotationX => "JoyRx",
        JoystickAxis.RotationY => "JoyRy",
        JoystickAxis.RotationZ => "JoyRz",
        JoystickAxis.Slider1 => "JoySlider1",
        JoystickAxis.Slider2 => "JoySlider2",
        _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, null),
    };

    public static string AxisDisplayName(JoystickAxis axis) => axis switch
    {
        JoystickAxis.X => "X",
        JoystickAxis.Y => "Y",
        JoystickAxis.Z => "Z",
        JoystickAxis.RotationX => "X rotation",
        JoystickAxis.RotationY => "Y rotation",
        JoystickAxis.RotationZ => "Z rotation",
        JoystickAxis.Slider1 => "Slider 1",
        JoystickAxis.Slider2 => "Slider 2",
        _ => axis.ToString(),
    };
}

/// <summary>
/// One plotted line: the variables it reads (x1..xN in <see cref="Formula"/>, in list order), the formula
/// combining them into the displayed value, and display options. Raw variable values are what's recorded;
/// the formula is applied only for display.
/// </summary>
public sealed record PlotLineSettings
{
    /// <summary>Descriptive name, shown in the Settings list and as the legend tooltip.</summary>
    public string Name { get; init; } = "";

    /// <summary>Compact name shown in the "Data &amp; Scale" legend and the cursor value label.</summary>
    public string ShortName { get; init; } = "";

    /// <summary>Line color as #RRGGBB or #AARRGGBB.</summary>
    public string Color { get; init; } = "#FFFFFF";

    public IReadOnlyList<PlotVariable> Variables { get; init; } = [];

    public string Formula { get; init; } = "x1";

    /// <summary>Default lower bound of the displayed range (also what zoom resets to when settings are applied).</summary>
    public double Min { get; init; }

    /// <summary>Default upper bound of the displayed range.</summary>
    public double Max { get; init; } = 1;

    public ScrollScaleMode ScaleMode { get; init; } = ScrollScaleMode.Both;

    /// <summary>Display-only unit suffix for the value label and range text, e.g. "°", "ft".</summary>
    public string Unit { get; init; } = "";

    /// <summary>Decimal places for the value label and range text.</summary>
    public int Decimals { get; init; } = 1;
}
