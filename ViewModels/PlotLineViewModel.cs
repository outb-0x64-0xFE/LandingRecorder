using System.Globalization;
using LandingRecorder.Formulas;
using LandingRecorder.Models;
using LandingRecorder.Mvvm;

namespace LandingRecorder.ViewModels;

/// <summary>
/// Runtime state of one plotted line in the main window: its compiled formula, current (zoomable) display
/// range, visibility, and whether the displayed data actually contains its variables.
/// </summary>
public sealed class PlotLineViewModel : ObservableObject
{
    private const double ZoomFactor = 1.1;
    private const double MinSpan = 1e-9;

    private readonly Formula? _formula;
    private readonly double[] _args;
    private readonly double[] _displayFactors;
    private double _min;
    private double _max;
    private bool _isVisible = true;
    private bool _isAvailable = true;
    private string? _unavailableReason;

    public PlotLineViewModel(PlotLineSettings settings)
    {
        Settings = settings;
        _min = settings.Min;
        _max = settings.Max;
        _args = new double[settings.Variables.Count];
        _displayFactors = settings.Variables.Select(variable => variable.DisplayFactor).ToArray();

        // Settings validation normally guarantees this compiles; a hand-edited settings.json might not.
        if (Formula.TryCompile(settings.Formula, settings.Variables.Count, out var formula, out var error))
        {
            _formula = formula;
        }
        else
        {
            AppLogger.WriteLine($"Plot line '{settings.Name}': invalid formula ignored: {error}");
            SetAvailability(false, $"Invalid formula: {error}");
        }
    }

    public PlotLineSettings Settings { get; }

    public string Name => Settings.Name;

    /// <summary>Shown in the legend and cursor label; falls back to <see cref="Name"/> if a hand-edited settings.json left it empty.</summary>
    public string ShortName => string.IsNullOrWhiteSpace(Settings.ShortName) ? Settings.Name : Settings.ShortName;

    public string Color => Settings.Color;

    public double Min => _min;

    public double Max => _max;

    public string RangeText
    {
        get
        {
            var suffix = FormatUnitSuffix(Settings.Unit);
            // Tolerance: repeated symmetric zooms can leave the center a rounding error away from zero.
            var isSymmetric = Math.Abs(_min + _max) <= (_max - _min) * 1e-9;
            return isSymmetric
                ?$"±{Format(_max)}{suffix}"
                : $"{Format(_min)}–{Format(_max)}{suffix}";
        }
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    /// <summary>False when the displayed recording lacks one of this line's variables (or the formula is invalid).</summary>
    public bool IsAvailable => _isAvailable;

    public string? UnavailableReason => _unavailableReason;

    /// <summary>Whether the graph should draw this line at all.</summary>
    public bool ShouldDraw => _isVisible && _isAvailable && _formula is not null;

    /// <summary>
    /// The line's display value at <paramref name="point"/>, or null if any variable is missing at that
    /// point or the result isn't finite (e.g. sqrt of a negative number).
    /// </summary>
    public double? Evaluate(DataPoint point)
    {
        if (_formula is null)
        {
            return null;
        }

        var variables = Settings.Variables;
        for (var i = 0; i < variables.Count; i++)
        {
            if (!point.Values.TryGetValue(variables[i].RecordKey, out var recorded))
            {
                return null;
            }

            // Recorded joystick axes are percent; degree-scaled axes are converted here, at display time.
            _args[i] = recorded * _displayFactors[i];
        }

        var value = _formula.Evaluate(_args);
        return double.IsFinite(value) ? value : null;
    }

    public string FormatValue(double value) => $"{ShortName} {Format(value)}{FormatUnitSuffix(Settings.Unit)}";

    /// <summary>Wheel up (positive delta) zooms in, shrinking the range per <see cref="PlotLineSettings.ScaleMode"/>.</summary>
    public void Zoom(int wheelDelta)
    {
        var newSpan = ZoomedSpan(wheelDelta);

        switch (Settings.ScaleMode)
        {
            case ScrollScaleMode.MinOnly:
                _min = _max - newSpan;
                break;
            case ScrollScaleMode.MaxOnly:
                _max = _min + newSpan;
                break;
            default:
                var center = (_min + _max) / 2;
                _min = center - newSpan / 2;
                _max = center + newSpan / 2;
                break;
        }

        OnPropertyChanged(nameof(Min), nameof(Max), nameof(RangeText));
    }

    /// <summary>
    /// Zooms like <see cref="Zoom"/> (same step) but keeps <paramref name="anchorValue"/> at the same relative
    /// position in the range, so its on-screen position doesn't move. Used when scrolling over the line's
    /// handle on the graph, so the handle stays under the mouse; ignores <see cref="PlotLineSettings.ScaleMode"/>
    /// since the anchor, not a range bound, is what stays fixed.
    /// </summary>
    public void ZoomAround(int wheelDelta, double anchorValue)
    {
        var span = _max - _min;
        var anchorFraction = Math.Clamp((anchorValue - _min) / span, 0, 1);
        var newSpan = ZoomedSpan(wheelDelta);
        var anchor = _min + anchorFraction * span;

        _min = anchor - anchorFraction * newSpan;
        _max = _min + newSpan;
        OnPropertyChanged(nameof(Min), nameof(Max), nameof(RangeText));
    }

    private double ZoomedSpan(int wheelDelta) =>
        Math.Max(MinSpan, (_max - _min) * (wheelDelta > 0 ? 1 / ZoomFactor : ZoomFactor));

    /// <summary>Restores the default range from settings (right-click on the line's graph handle).</summary>
    public void ResetRange() => SetRange(Settings.Min, Settings.Max);

    /// <summary>Sets the display range directly (used when dragging the line's handle on the graph to pan it).</summary>
    public void SetRange(double min, double max)
    {
        if (!(max - min >= MinSpan) || !double.IsFinite(min) || !double.IsFinite(max))
        {
            return;
        }

        _min = min;
        _max = max;
        OnPropertyChanged(nameof(Min), nameof(Max), nameof(RangeText));
    }

    /// <summary>
    /// Matches this line against the variables present in the displayed data (name → units). A null map
    /// means "no recording loaded" (live/empty), where every line is available.
    /// </summary>
    public void UpdateAvailability(IReadOnlyDictionary<string, string>? recordedVariables)
    {
        if (_formula is null)
        {
            return; // Stays unavailable with its formula error.
        }

        if (recordedVariables is null)
        {
            SetAvailability(true, null);
            return;
        }

        foreach (var variable in Settings.Variables)
        {
            if (!recordedVariables.TryGetValue(variable.RecordKey, out var recordedUnits))
            {
                SetAvailability(false, $"'{variable.DisplayName}' is not in the recording");
                return;
            }

            var expectedUnits = variable.IsJoystickAxis ? PlotVariable.JoystickUnits : variable.Units;
            if (!string.Equals(recordedUnits, expectedUnits, StringComparison.OrdinalIgnoreCase))
            {
                SetAvailability(false, $"'{variable.DisplayName}' was recorded in {recordedUnits}, not {expectedUnits}");
                return;
            }
        }

        SetAvailability(true, null);
    }

    private void SetAvailability(bool isAvailable, string? reason)
    {
        _isAvailable = isAvailable;
        _unavailableReason = reason;
        OnPropertyChanged(nameof(IsAvailable), nameof(UnavailableReason));
    }

    private string Format(double value) =>
        value.ToString("F" + Math.Clamp(Settings.Decimals, 0, 10), CultureInfo.CurrentCulture);

    // Symbol-like units hug the number ("3.1°"); word units get a space ("96.9 ft").
    private static string FormatUnitSuffix(string unit) =>
        string.IsNullOrEmpty(unit) ? "" : unit is "°" or "%" ? unit : " " + unit;
}
