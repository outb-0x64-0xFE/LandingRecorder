using System.IO;
using System.Text.Json;
using LandingRecorder.Models;

namespace LandingRecorder.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "settings.json");

    /// <summary>Loads settings.json, falling back to defaults if it doesn't exist or can't be parsed.</summary>
    public AppSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            AppLogger.WriteLine($"Settings file not found ({FilePath}); using defaults.");
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            AppLogger.WriteLine($"Failed to load settings ({FilePath}); using defaults: {ex.Message}");
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(FilePath, json);
        AppLogger.WriteLine($"Settings saved: {FilePath}");
    }
}
