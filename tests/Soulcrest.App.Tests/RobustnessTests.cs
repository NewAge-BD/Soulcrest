using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>A held file must not end capture or tracking (full code review 2026-10-07).</summary>
public sealed class RobustnessTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "soulcrest-robust-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ALockedLogIsSkippedInsteadOfThrowing()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "map-tracking.log");
        File.WriteAllText(path, new string('a', 200));
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            LogFile.Append(_directory, "map-tracking.log", "b", 100); // the rotation and the append both fail
        LogFile.Append(_directory, "map-tracking.log", "c", 100);
        Assert.Equal("c", File.ReadAllText(path));
    }

    [Fact]
    public void ASaveWaitsForAFileHeldForAMoment()
    {
        var path = Path.Combine(_directory, "progress.json");
        JsonFile.Save(path, new Dictionary<string, int> { ["a"] = 1 });
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        _ = Task.Delay(120).ContinueWith(_ => held.Dispose());
        JsonFile.Save(path, new Dictionary<string, int> { ["a"] = 2 });
        Assert.Equal(2, JsonFile.Load<Dictionary<string, int>>(path)["a"]);
    }

    [Fact]
    public void AThrowingSubscriberDoesNotStopTheOthers()
    {
        var reached = 0;
        Action? changed = null;
        changed += () => throw new IOException("locked");
        changed += () => reached++;
        SafeEvent.Raise(changed, "test");
        Assert.Equal(1, reached);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
