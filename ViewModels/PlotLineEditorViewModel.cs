using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Input;
using LandingRecorder.Formulas;
using LandingRecorder.Models;
using LandingRecorder.Mvvm;

namespace LandingRecorder.ViewModels;

/// <summary>
/// One entry of the joystick-axis picker. The Settings window shares one fixed list of all standard axes
/// across every variable row and only updates <see cref="IsAvailable"/> when the joystick selection changes —
/// never rebuilding the list, which would make bound ComboBoxes drop (and write back) their selection.
/// Axes the selected device doesn't have stay listed but disabled, so a saved choice survives an unplugged device.
/// </summary>
public sealed class JoystickAxisOption(JoystickAxis axis) : ObservableObject
{
    private bool _isAvailable = true;

    public JoystickAxis Axis { get; } = axis;

    public bool IsAvailable
    {
        get => _isAvailable;
        set
        {
            if (SetProperty(ref _isAvailable, value))
            {
                OnPropertyChanged(nameof(Label));
            }
        }
    }

    public string Label => IsAvailable
        ? PlotVariable.AxisDisplayName(Axis)
        : $"{PlotVariable.AxisDisplayName(Axis)} (not on device)";
}

/// <summary>Editable row for one variable (x1..xN) of a plot line: a simvar (name + units) or a joystick axis.</summary>
public sealed class VariableEditorViewModel : ObservableObject
{
    public static IReadOnlyList<string> SourceOptions { get; } = ["SimVar", "Joystick axis"];

    public static IReadOnlyList<string> ScaleOptions { get; } = ["Percent", "Degrees"];

    private string _label = "";
    private int _sourceIndex;
    private string _name;
    private string _units;
    private JoystickAxis _axis;
    private int _scaleIndex;
    private string _maxDeflectionText;

    public VariableEditorViewModel(PlotVariable variable, IReadOnlyList<JoystickAxisOption> axisOptions, Action<VariableEditorViewModel> remove)
    {
        _sourceIndex = variable.IsJoystickAxis ? 1 : 0;
        _name = variable.Name ?? "";
        _units = variable.Units ?? "";
        _axis = variable.Axis ?? JoystickAxis.Y;
        _scaleIndex = variable.Scale == AxisScale.Degrees ? 1 : 0;
        _maxDeflectionText = (variable.MaxDeflectionDeg ?? PlotVariable.DefaultMaxDeflectionDeg).ToString(CultureInfo.CurrentCulture);
        AxisOptions = axisOptions;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    /// <summary>Formula label for this row's position, e.g. "x1".</summary>
    public string Label
    {
        get => _label;
        set => SetProperty(ref _label, value);
    }

    /// <summary>0 = SimVar, 1 = Joystick axis (index into <see cref="SourceOptions"/>).</summary>
    public int SourceIndex
    {
        get => _sourceIndex;
        set
        {
            if (SetProperty(ref _sourceIndex, value))
            {
                OnPropertyChanged(nameof(IsSimVar), nameof(IsJoystickAxis), nameof(ShowPercentRange), nameof(ShowMaxDeflection));
            }
        }
    }

    public bool IsSimVar => SourceIndex == 0;

    public bool IsJoystickAxis => SourceIndex == 1;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Units
    {
        get => _units;
        set => SetProperty(ref _units, value);
    }

    public JoystickAxis Axis
    {
        get => _axis;
        set => SetProperty(ref _axis, value);
    }

    /// <summary>0 = Percent, 1 = Degrees (index into <see cref="ScaleOptions"/>).</summary>
    public int ScaleIndex
    {
        get => _scaleIndex;
        set
        {
            if (SetProperty(ref _scaleIndex, value))
            {
                OnPropertyChanged(nameof(IsDegrees), nameof(IsPercent), nameof(ShowPercentRange), nameof(ShowMaxDeflection));
            }
        }
    }

    public bool IsDegrees => ScaleIndex == 1;

    public bool IsPercent => ScaleIndex == 0;

    public bool ShowPercentRange => IsJoystickAxis && IsPercent;

    public bool ShowMaxDeflection => IsJoystickAxis && IsDegrees;

    /// <summary>Full deflection in degrees, used when the scale is Degrees.</summary>
    public string MaxDeflectionText
    {
        get => _maxDeflectionText;
        set => SetProperty(ref _maxDeflectionText, value);
    }

    public IReadOnlyList<JoystickAxisOption> AxisOptions { get; }

    public ICommand RemoveCommand { get; }

    /// <summary>First problem with this row's joystick-specific fields, or null.</summary>
    public string? ValidateJoystick() =>
        IsJoystickAxis && IsDegrees
        && (!double.TryParse(MaxDeflectionText, NumberStyles.Float, CultureInfo.CurrentCulture, out var max) || !double.IsFinite(max) || max <= 0 || max > 180)
            ? $"{Label}: max deflection must be a number between 0 and 180°."
            : null;

    /// <summary>Only valid after validation passed.</summary>
    public PlotVariable ToSettings() => IsJoystickAxis
        ? PlotVariable.Joystick(
            Axis,
            IsDegrees ? AxisScale.Degrees : AxisScale.Percent,
            IsDegrees ? double.Parse(MaxDeflectionText, NumberStyles.Float, CultureInfo.CurrentCulture) : null)
        : PlotVariable.SimVar(Name.Trim(), Units.Trim());
}

/// <summary>
/// Editable working copy of one <see cref="PlotLineSettings"/> in the Settings window. Numeric fields are
/// bound as strings so invalid input can be reported. Raises <see cref="Changed"/> on any edit (including
/// variable rows) so the owning <see cref="SettingsViewModel"/> can re-validate everything.
/// </summary>
public sealed partial class PlotLineEditorViewModel : ObservableObject
{
    public static IReadOnlyList<string> ScaleModeOptions { get; } = ["Min and max", "Min only", "Max only"];

    /// <summary>
    /// Colors offered by the Settings color popup: 8 columns x 3 rows, all bright enough to read
    /// on the dark graph background. Row 1 holds the default plot lines' colors plus red/yellow,
    /// row 2 lighter tints of the same hues, row 3 extra distinct hues and two neutrals.
    /// </summary>
    public static IReadOnlyList<string> ColorPalette { get; } =
    [
        "#FF5252", "#FFB347", "#FFE14D", "#65DC91", "#00E5FF", "#47B3FF", "#BA68FF", "#FF57BB",
        "#FF9E9E", "#FFD199", "#FFF29E", "#B4F5A0", "#A0F4FF", "#A8D4FF", "#DDB8FF", "#FFA8DC",
        "#FF8A65", "#FFCA28", "#C6FF00", "#1DE9B6", "#EAF4FF", "#8C9EFF", "#E040FB", "#B0BEC5",
    ];

    private static readonly ScrollScaleMode[] ScaleModes = [ScrollScaleMode.Both, ScrollScaleMode.MinOnly, ScrollScaleMode.MaxOnly];

    private string _name;
    private string _shortName;
    private string _color;
    private string _formula;
    private string _minText;
    private string _maxText;
    private int _scaleModeIndex;
    private string _unit;
    private string _decimalsText;

    private readonly IReadOnlyList<JoystickAxisOption> _axisOptions;

    public PlotLineEditorViewModel(PlotLineSettings settings, IReadOnlyList<JoystickAxisOption> axisOptions)
    {
        _axisOptions = axisOptions;
        _name = settings.Name;
        _shortName = settings.ShortName;
        _color = settings.Color;
        _formula = settings.Formula;
        _minText = Format(settings.Min);
        _maxText = Format(settings.Max);
        _scaleModeIndex = Math.Max(0, Array.IndexOf(ScaleModes, settings.ScaleMode));
        _unit = settings.Unit;
        _decimalsText = settings.Decimals.ToString(CultureInfo.CurrentCulture);

        Variables.CollectionChanged += Variables_CollectionChanged;
        foreach (var variable in settings.Variables)
        {
            Variables.Add(new VariableEditorViewModel(variable, _axisOptions, RemoveVariable));
        }

        AddVariableCommand = new RelayCommand(() => Variables.Add(new VariableEditorViewModel(new PlotVariable(), _axisOptions, RemoveVariable)));
    }

    public event EventHandler? Changed;

    public ObservableCollection<VariableEditorViewModel> Variables { get; } = [];

    public ICommand AddVariableCommand { get; }

    public string Name
    {
        get => _name;
        set { if (SetProperty(ref _name, value)) OnChanged(); }
    }

    public string ShortName
    {
        get => _shortName;
        set { if (SetProperty(ref _shortName, value)) OnChanged(); }
    }

    public string Color
    {
        get => _color;
        set { if (SetProperty(ref _color, value)) OnChanged(); }
    }

    public string Formula
    {
        get => _formula;
        set { if (SetProperty(ref _formula, value)) OnChanged(); }
    }

    public string MinText
    {
        get => _minText;
        set { if (SetProperty(ref _minText, value)) OnChanged(); }
    }

    public string MaxText
    {
        get => _maxText;
        set { if (SetProperty(ref _maxText, value)) OnChanged(); }
    }

    public int ScaleModeIndex
    {
        get => _scaleModeIndex;
        set { if (SetProperty(ref _scaleModeIndex, value)) OnChanged(); }
    }

    public string Unit
    {
        get => _unit;
        set { if (SetProperty(ref _unit, value)) OnChanged(); }
    }

    public string DecimalsText
    {
        get => _decimalsText;
        set { if (SetProperty(ref _decimalsText, value)) OnChanged(); }
    }

    /// <summary>Hint listing the labels usable in the formula, e.g. "Variables: x1, x2".</summary>
    public string VariablesHint => Variables.Count == 0
        ? "Add at least one variable."
        : $"Use {string.Join(", ", Variables.Select(variable => variable.Label))} in the formula.";

    /// <summary>First problem with this line, or null if it's valid.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            return "Name is required.";
        }

        if (string.IsNullOrWhiteSpace(ShortName))
        {
            return "Short name is required.";
        }

        if (!HexColorRegex().IsMatch(Color.Trim()))
        {
            return "Color must be #RRGGBB or #AARRGGBB.";
        }

        if (Variables.Count == 0)
        {
            return "At least one variable is required.";
        }

        foreach (var variable in Variables.Where(variable => variable.IsSimVar))
        {
            if (string.IsNullOrWhiteSpace(variable.Name))
            {
                return $"{variable.Label}: simvar name is required.";
            }

            if (string.IsNullOrWhiteSpace(variable.Units))
            {
                return $"{variable.Label}: units are required (e.g. feet, degrees).";
            }
        }

        foreach (var variable in Variables)
        {
            if (variable.ValidateJoystick() is string joystickError)
            {
                return joystickError;
            }
        }

        if (!LandingRecorder.Formulas.Formula.TryCompile(Formula, Variables.Count, out _, out var formulaError))
        {
            return $"Formula: {formulaError}";
        }

        if (!TryParse(MinText, out var min) || !TryParse(MaxText, out var max))
        {
            return "Min and max must be numbers.";
        }

        if (min >= max)
        {
            return "Min must be less than max.";
        }

        if (!int.TryParse(DecimalsText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var decimals) || decimals < 0 || decimals > 6)
        {
            return "Decimals must be a whole number from 0 to 6.";
        }

        return null;
    }

    /// <summary>Only valid after <see cref="Validate"/> returned null.</summary>
    public PlotLineSettings ToSettings() => new()
    {
        Name = Name.Trim(),
        ShortName = ShortName.Trim(),
        Color = Color.Trim(),
        Variables = Variables.Select(variable => variable.ToSettings()).ToList(),
        Formula = Formula.Trim(),
        Min = double.Parse(MinText, NumberStyles.Float, CultureInfo.CurrentCulture),
        Max = double.Parse(MaxText, NumberStyles.Float, CultureInfo.CurrentCulture),
        ScaleMode = ScaleModes[Math.Clamp(ScaleModeIndex, 0, ScaleModes.Length - 1)],
        Unit = Unit.Trim(),
        Decimals = int.Parse(DecimalsText, NumberStyles.Integer, CultureInfo.CurrentCulture),
    };

    private void RemoveVariable(VariableEditorViewModel variable) => Variables.Remove(variable);

    private void Variables_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (VariableEditorViewModel item in e.OldItems)
            {
                item.PropertyChanged -= Variable_PropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (VariableEditorViewModel item in e.NewItems)
            {
                item.PropertyChanged += Variable_PropertyChanged;
            }
        }

        for (var i = 0; i < Variables.Count; i++)
        {
            Variables[i].Label = LandingRecorder.Formulas.Formula.VariableLabel(i);
        }

        OnPropertyChanged(nameof(VariablesHint));
        OnChanged();
    }

    private void Variable_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(VariableEditorViewModel.Label))
        {
            OnChanged();
        }
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static string Format(double value) => value.ToString(CultureInfo.CurrentCulture);

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value);

    [GeneratedRegex("^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$")]
    private static partial Regex HexColorRegex();
}
