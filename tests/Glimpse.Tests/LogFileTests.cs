using Glimpse.Core.Diagnostics;
using Xunit;

namespace Glimpse.Tests;

public sealed class LogFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("glimpse-log").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ALogPastTheLimitIsSetAsideReplacingTheOldOne()
    {
        var log = Path.Combine(_dir, "glimpse.log");
        var old = Path.Combine(_dir, "glimpse.old.log");
        Assert.Equal(old, LogFile.OldPath(log));
        File.WriteAllText(old, "older");
        File.WriteAllText(log, "0123456789");   // 10 bytes

        Assert.False(LogFile.RollOver(log, maxBytes: 10));   // at the limit: kept
        Assert.True(File.Exists(log));
        Assert.True(LogFile.RollOver(log, maxBytes: 9));
        Assert.False(File.Exists(log));
        Assert.Equal("0123456789", File.ReadAllText(old));
        Assert.False(LogFile.RollOver(log, maxBytes: 9));    // no log yet: nothing to do
    }

    [Fact]
    public void ALogThatCannotBeMovedIsKept()
    {
        var log = Path.Combine(_dir, "glimpse.log");
        File.WriteAllText(log, "0123456789");
        using (new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.False(LogFile.RollOver(log, maxBytes: 1));
        Assert.True(File.Exists(log));
        Assert.Equal(5_000_000, LogFile.MaxBytes);

        // An old log that may not be replaced (read-only) also leaves the current one in place.
        var old = LogFile.OldPath(log);
        File.WriteAllText(old, "older");
        File.SetAttributes(old, FileAttributes.ReadOnly);
        try
        {
            Assert.False(LogFile.RollOver(log, maxBytes: 1));
            Assert.True(File.Exists(log));
        }
        finally
        {
            File.SetAttributes(old, FileAttributes.Normal);
        }
    }
}
