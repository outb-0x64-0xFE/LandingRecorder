using System.Text.Json.Serialization;

namespace LandingRecorder.Models;

/// <summary>
/// One recorded sample. <see cref="Values"/> holds raw variable values keyed by <see cref="PlotVariable.RecordKey"/>
/// (the simvar name, or e.g. "JoyY" for a joystick axis); a variable that had no value at that moment is
/// simply absent. <see cref="TouchdownSim"/> is always recorded regardless of settings.
/// </summary>
public sealed record DataPoint(
    double TimeSeconds,
    IReadOnlyDictionary<string, double> Values,
    // Edge-triggered event flag: omitted from the JSON except on the single point where it's true.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool TouchdownSim = false);
