using System.Text.Json;
using LandingRecorder.Formulas;
using LandingRecorder.Models;
using Xunit;

namespace LandingRecorder.Tests;

public class ModelTests
{
    [Fact]
    public void DefaultPlotLinesHaveValidFormulasAndRanges()
    {
        foreach (var line in AppSettings.DefaultPlotLines)
        {
            Assert.True(Formula.TryCompile(line.Formula, line.Variables.Count, out _, out var error), $"{line.Name}: {error}");
            Assert.True(line.Min < line.Max, line.Name);
            Assert.False(string.IsNullOrWhiteSpace(line.ShortName), line.Name);
        }
    }

    [Fact]
    public void RecordedSimVarsAndAxesAreUniqueAndSeparated()
    {
        var settings = new AppSettings
        {
            PlotLines =
            [
                new() { Name = "A", Variables = [PlotVariable.SimVar("G FORCE", "GForce"), PlotVariable.Joystick(JoystickAxis.Y)] },
                new() { Name = "B", Variables = [PlotVariable.SimVar("g force", "GForce"), PlotVariable.SimVar("PLANE PITCH DEGREES", "degrees"), PlotVariable.Joystick(JoystickAxis.Y), PlotVariable.Joystick(JoystickAxis.RotationZ)] },
            ],
        };

        Assert.Equal(["G FORCE", "PLANE PITCH DEGREES"], settings.GetRecordedSimVars().Select(variable => variable.Name).ToList());
        Assert.Equal([JoystickAxis.Y, JoystickAxis.RotationZ], settings.GetRecordedJoystickAxes());
    }

    [Theory]
    [InlineData(JoystickAxis.X, "JoyX")]
    [InlineData(JoystickAxis.Y, "JoyY")]      // matches the key in recordings made before axes were selectable
    [InlineData(JoystickAxis.RotationZ, "JoyRz")]
    [InlineData(JoystickAxis.Slider2, "JoySlider2")]
    public void JoystickAxesHaveStableRecordKeys(JoystickAxis axis, string expectedKey)
    {
        Assert.Equal(expectedKey, PlotVariable.Joystick(axis).RecordKey);
    }

    [Fact]
    public void PlotVariablesSerializeOnlyTheirSourceFields()
    {
        var simVarJson = JsonSerializer.Serialize(PlotVariable.SimVar("G FORCE", "GForce"));
        var percentAxisJson = JsonSerializer.Serialize(PlotVariable.Joystick(JoystickAxis.Y));
        var degreesAxisJson = JsonSerializer.Serialize(PlotVariable.Joystick(JoystickAxis.Y, AxisScale.Degrees, 30));

        Assert.Equal("""{"Source":"SimVar","Name":"G FORCE","Units":"GForce"}""", simVarJson);
        Assert.Equal("""{"Source":"JoystickAxis","Axis":"Y","Scale":"Percent"}""", percentAxisJson);
        Assert.Equal("""{"Source":"JoystickAxis","Axis":"Y","Scale":"Degrees","MaxDeflectionDeg":30}""", degreesAxisJson);
        Assert.Equal(PlotVariable.Joystick(JoystickAxis.Y, AxisScale.Degrees, 30), JsonSerializer.Deserialize<PlotVariable>(degreesAxisJson));
    }

    [Fact]
    public void DisplayFactorConvertsRecordedPercentForDegreeAxesOnly()
    {
        Assert.Equal(1.0, PlotVariable.SimVar("G FORCE", "GForce").DisplayFactor);
        Assert.Equal(1.0, PlotVariable.Joystick(JoystickAxis.X).DisplayFactor);
        Assert.Equal(0.25, PlotVariable.Joystick(JoystickAxis.X, AxisScale.Degrees, 25).DisplayFactor); // 100 % -> 25 deg
    }

    [Fact]
    public void SettingsWithoutPlotLinesFallBackToDefaults()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{ "AutoStartHeightFeet": 50 }""")!;
        Assert.Equal(50, settings.AutoStartHeightFeet);
        Assert.Equal(AppSettings.DefaultPlotLines.Count, settings.PlotLines.Count);
    }

    [Fact]
    public void RecordingsFolderDefaultsToExecutableFolder()
    {
        Assert.Null(new AppSettings().RecordingsFolder);
        Assert.Equal(AppContext.BaseDirectory, new AppSettings().GetRecordingsFolder());
        Assert.Equal(AppContext.BaseDirectory, new AppSettings { RecordingsFolder = "  " }.GetRecordingsFolder());
        Assert.Equal(@"D:\Recordings", new AppSettings { RecordingsFolder = @"D:\Recordings" }.GetRecordingsFolder());
    }

    [Fact]
    public void SettingsRoundTripKeepsPlotLines()
    {
        var original = new AppSettings
        {
            PlotLines = [new() { Name = "Bank", ShortName = "Bank", Variables = [PlotVariable.SimVar("PLANE BANK DEGREES", "degrees")], ScaleMode = ScrollScaleMode.MinOnly }],
        };

        var json = JsonSerializer.Serialize(original);
        Assert.Contains("\"MinOnly\"", json); // enum stored by name
        var loaded = JsonSerializer.Deserialize<AppSettings>(json)!;

        var line = Assert.Single(loaded.PlotLines);
        Assert.Equal("Bank", line.Name);
        Assert.Equal(ScrollScaleMode.MinOnly, line.ScaleMode);
        Assert.Equal("PLANE BANK DEGREES", Assert.Single(line.Variables).Name);
    }

    [Fact]
    public void RecordingRoundTripsAndLooksUpNamesCaseInsensitively()
    {
        var path = Path.Combine(Path.GetTempPath(), $"recording-test-{Guid.NewGuid():N}.json");
        try
        {
            new Recording(
                new Dictionary<string, string> { ["G FORCE"] = "GForce" },
                [
                    new DataPoint(0.0, new Dictionary<string, double> { ["G FORCE"] = 1.0 }),
                    new DataPoint(0.1, new Dictionary<string, double> { ["G FORCE"] = 1.5 }, TouchdownSim: true),
                ]).Save(path);

            var json = File.ReadAllText(path);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "TouchdownSim")); // only written when true

            var loaded = Recording.Load(path);
            Assert.Equal("GForce", loaded.Variables["g force"]);
            Assert.Equal(2, loaded.Points.Count);
            Assert.Equal(1.5, loaded.Points[1].Values["g force"]);
            Assert.False(loaded.Points[0].TouchdownSim);
            Assert.True(loaded.Points[1].TouchdownSim);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadingOldFormatRecordingFails()
    {
        var path = Path.Combine(Path.GetTempPath(), $"recording-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """[ { "TimeSeconds": 0, "Height": 100 } ]""");
            Assert.ThrowsAny<Exception>(() => Recording.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
