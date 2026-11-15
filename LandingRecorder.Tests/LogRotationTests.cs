using Xunit;

namespace LandingRecorder.Tests;

public sealed class LogRotationTests : IDisposable
{
    private const int MaxArchives = 5;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"logrotation-{Guid.NewGuid():N}");
    private readonly string _logPath;

    public LogRotationTests()
    {
        Directory.CreateDirectory(_directory);
        _logPath = Path.Combine(_directory, "app.log");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Archive(int number) => Path.Combine(_directory, $"app-{number}.log");

    private void WriteLog(string content) => File.WriteAllText(_logPath, content);

    [Fact]
    public void FirstRotationCreatesArchiveOne()
    {
        WriteLog("first");
        LogRotation.Rotate(_logPath, MaxArchives);

        Assert.False(File.Exists(_logPath));
        Assert.Equal("first", File.ReadAllText(Archive(1)));
    }

    [Fact]
    public void LaterRotationsTakeTheNextNumber()
    {
        for (var i = 1; i <= 3; i++)
        {
            WriteLog($"log {i}");
            LogRotation.Rotate(_logPath, MaxArchives);
        }

        Assert.Equal("log 1", File.ReadAllText(Archive(1)));
        Assert.Equal("log 2", File.ReadAllText(Archive(2)));
        Assert.Equal("log 3", File.ReadAllText(Archive(3)));
        Assert.False(File.Exists(Archive(4)));
    }

    [Fact]
    public void WhenFullOldestIsRemovedAndOthersShiftDownKeepingOrder()
    {
        for (var i = 1; i <= 7; i++)
        {
            WriteLog($"log {i}");
            LogRotation.Rotate(_logPath, MaxArchives);
        }

        // Logs 1 and 2 were dropped as the oldest; 3..7 remain, oldest first.
        for (var number = 1; number <= MaxArchives; number++)
        {
            Assert.Equal($"log {number + 2}", File.ReadAllText(Archive(number)));
        }

        Assert.False(File.Exists(Archive(6)));
        Assert.False(File.Exists(_logPath));
    }

    [Fact]
    public void GapsAreCompactedWithoutReordering()
    {
        File.WriteAllText(Archive(2), "older");
        File.WriteAllText(Archive(4), "newer");
        WriteLog("newest");

        LogRotation.Rotate(_logPath, MaxArchives);

        Assert.Equal("older", File.ReadAllText(Archive(1)));
        Assert.Equal("newer", File.ReadAllText(Archive(2)));
        Assert.Equal("newest", File.ReadAllText(Archive(3)));
        Assert.False(File.Exists(Archive(4)));
    }
}
