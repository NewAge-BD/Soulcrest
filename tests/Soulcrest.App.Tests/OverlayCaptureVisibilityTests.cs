using Soulcrest.App.Overlay;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayCaptureVisibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedFramesApplyTheSameSuccessfulAffinityOnlyOnce(bool visible)
    {
        var calls = new List<(nint Handle, bool Visible)>();
        var state = new OverlayCaptureVisibility((handle, value) => { calls.Add((handle, value)); return true; });
        for (var frame = 0; frame < 100; frame++) Assert.True(state.Apply(123, visible));
        Assert.Equal(((nint)123, visible), Assert.Single(calls));
    }

    [Fact]
    public void ChangingVisibilityReappliesTheAffinity()
    {
        var calls = new List<bool>();
        var state = new OverlayCaptureVisibility((_, visible) => { calls.Add(visible); return true; });
        Assert.True(state.Apply(123, false));
        Assert.True(state.Apply(123, true));
        Assert.True(state.Apply(123, true));
        Assert.Equal(new[] { false, true }, calls);
    }

    [Fact]
    public void ChangingTheHandleReappliesEvenTheSameVisibility()
    {
        var calls = new List<nint>();
        var state = new OverlayCaptureVisibility((handle, _) => { calls.Add(handle); return true; });
        Assert.True(state.Apply(123, false));
        Assert.True(state.Apply(456, false));
        Assert.True(state.Apply(456, false));
        Assert.Equal(new nint[] { 123, 456 }, calls);
    }

    [Fact]
    public void ResetReappliesWhenWindowsReusesTheSameHandle()
    {
        var calls = 0;
        var state = new OverlayCaptureVisibility((_, _) => { calls++; return true; });
        Assert.True(state.Apply(123, false));
        state.Reset();
        Assert.True(state.Apply(123, false));
        Assert.True(state.Apply(123, false));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void FailedNativeCallsAreRetriedUntilTheySucceed()
    {
        var calls = 0;
        var state = new OverlayCaptureVisibility((_, _) => ++calls >= 2);
        Assert.False(state.Apply(123, false));
        Assert.True(state.Apply(123, false));
        Assert.True(state.Apply(123, false));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ThrowingNativeCallsAreNotCached()
    {
        var calls = 0;
        var state = new OverlayCaptureVisibility((_, _) => ++calls == 1
            ? throw new InvalidOperationException("synthetic native failure") : true);
        Assert.Throws<InvalidOperationException>(() => state.Apply(123, false));
        Assert.True(state.Apply(123, false));
        Assert.True(state.Apply(123, false));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void FailedChangesDoNotCacheAnUnconfirmedState()
    {
        var calls = new List<bool>();
        var state = new OverlayCaptureVisibility((_, visible) => { calls.Add(visible); return calls.Count != 2; });
        Assert.True(state.Apply(123, false));
        Assert.False(state.Apply(123, true));
        Assert.True(state.Apply(123, true));
        Assert.True(state.Apply(123, true));
        Assert.Equal(new[] { false, true, true }, calls);
    }

    [Fact]
    public void SeparateLiveWindowsHaveIndependentAffinityState()
    {
        var calls = 0;
        bool Apply(nint _, bool __) { calls++; return true; }
        var first = new OverlayCaptureVisibility(Apply);
        var second = new OverlayCaptureVisibility(Apply);
        Assert.True(first.Apply(123, false));
        Assert.True(second.Apply(456, false));
        Assert.Equal(2, calls);
    }
}
