using System.IO;
using System.Text.Json;

namespace LandingRecorder.Models;

/// <summary>
/// The recording JSON file: a header mapping each recorded variable name to the units it was recorded in,
/// plus the points. The header lets a loaded recording be matched against the current plot-line settings —
/// a line whose variable is missing from the recording (or was recorded in different units) isn't plotted.
/// </summary>
public sealed record Recording(IReadOnlyDictionary<string, string> Variables, IReadOnlyList<DataPoint> Points)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Recording Load(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var recording = JsonSerializer.Deserialize<Recording>(json, JsonOptions)
            ?? throw new InvalidDataException("File contains no recording.");

        if (recording.Variables is null || recording.Points is null)
        {
            throw new InvalidDataException("File is not a recording (missing 'Variables' or 'Points').");
        }

        // Re-key case-insensitively so lookups match settings regardless of how the names were typed.
        return new Recording(
            new Dictionary<string, string>(recording.Variables, StringComparer.OrdinalIgnoreCase),
            recording.Points.Select(point => point with
            {
                Values = new Dictionary<string, double>(point.Values ?? new Dictionary<string, double>(), StringComparer.OrdinalIgnoreCase),
            }).ToList());
    }

    public void Save(string filePath)
    {
        File.WriteAllText(filePath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
