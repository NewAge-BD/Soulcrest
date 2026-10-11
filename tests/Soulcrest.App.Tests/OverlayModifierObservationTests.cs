using Soulcrest.App.Overlay;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayModifierObservationTests
{
    [Fact]
    public void RawReleaseClearsAStaleHookPressEvenWhenRawWasAlreadyUp()
    {
        var raw = new OverlayKeyboardInput();
        using var hook = new OverlayModifierHook();
        raw.ProcessKeyboard(1, OverlayKeyboardInput.LeftAltKey, 0x38, OverlayKeyboardInput.KeyBreak);
        hook.ProcessTransition(OverlayModifierHook.KeyDown, OverlayModifierHook.LeftAltKey, 0x38, 0);
        Assert.True(Held(hook.AltObservation, raw.AltObservation));

        raw.ProcessKeyboard(1, OverlayKeyboardInput.LeftAltKey, 0x38, OverlayKeyboardInput.KeyBreak);
        Assert.False(raw.AltHeld);
        Assert.True(hook.AltHeld); // Reproduces the contradictory live diagnostic state.
        Assert.False(Held(hook.AltObservation, raw.AltObservation));
    }

    [Fact]
    public void HookReleaseClearsAStaleRawPressEvenWhenHookWasAlreadyUp()
    {
        var raw = new OverlayKeyboardInput();
        using var hook = new OverlayModifierHook();
        hook.ProcessTransition(OverlayModifierHook.KeyUp, OverlayModifierHook.LeftAltKey, 0x38, 0);
        raw.ProcessKeyboard(1, OverlayKeyboardInput.LeftAltKey, 0x38, 0);
        Assert.True(Held(hook.AltObservation, raw.AltObservation));

        hook.ProcessTransition(OverlayModifierHook.KeyUp, OverlayModifierHook.LeftAltKey, 0x38, 0);
        Assert.True(raw.AltHeld);
        Assert.False(hook.AltHeld);
        Assert.False(Held(hook.AltObservation, raw.AltObservation));
    }

    [Fact]
    public void NewPressFromEitherSourceSupersedesTheOtherSourcesOldRelease()
    {
        var raw = new OverlayKeyboardInput();
        using var hook = new OverlayModifierHook();
        raw.ProcessKeyboard(1, OverlayKeyboardInput.LeftAltKey, 0x38, OverlayKeyboardInput.KeyBreak);
        hook.ProcessTransition(OverlayModifierHook.KeyDown, OverlayModifierHook.LeftAltKey, 0x38, 0);
        Assert.True(Held(hook.AltObservation, raw.AltObservation));
        hook.ProcessTransition(OverlayModifierHook.KeyUp, OverlayModifierHook.LeftAltKey, 0x38, 0);
        Assert.False(Held(hook.AltObservation, raw.AltObservation));
        raw.ProcessKeyboard(1, OverlayKeyboardInput.LeftAltKey, 0x38, 0);
        Assert.True(Held(hook.AltObservation, raw.AltObservation));
    }

    [Fact]
    public void ShiftObservationsCannotClearAltAndAltObservationsCannotClearShift()
    {
        var raw = new OverlayKeyboardInput();
        using var hook = new OverlayModifierHook();
        raw.ProcessKeyboard(1, OverlayKeyboardInput.LeftAltKey, 0x38, OverlayKeyboardInput.KeyBreak);
        hook.ProcessTransition(OverlayModifierHook.KeyDown, OverlayModifierHook.LeftAltKey, 0x38, 0);
        raw.ProcessKeyboard(1, OverlayKeyboardInput.LeftShiftKey, 0x2a, OverlayKeyboardInput.KeyBreak);
        Assert.True(Held(hook.AltObservation, raw.AltObservation));
        hook.ProcessTransition(OverlayModifierHook.KeyDown, OverlayModifierHook.LeftShiftKey, 0x2a, 0);
        raw.ProcessKeyboard(1, OverlayKeyboardInput.LeftAltKey, 0x38, OverlayKeyboardInput.KeyBreak);
        Assert.False(Held(hook.AltObservation, raw.AltObservation));
        Assert.True(Held(hook.ShiftObservation, raw.ShiftObservation));
    }

    [Fact]
    public void RemovingTheHeldKeyboardPublishesAReleaseAndStopDiscardsItsObservation()
    {
        var raw = new OverlayKeyboardInput();
        using var hook = new OverlayModifierHook();
        raw.ProcessKeyboard(1, OverlayKeyboardInput.LeftAltKey, 0x38, 0);
        hook.ProcessTransition(OverlayModifierHook.KeyDown, OverlayModifierHook.LeftAltKey, 0x38, 0);
        raw.RemoveDevice(1);
        Assert.False(Held(hook.AltObservation, raw.AltObservation));
        raw.Stop();
        Assert.Null(raw.AltObservation.Held);
        Assert.True(Held(hook.AltObservation, raw.AltObservation));
        hook.Stop();
        Assert.Null(OverlayModifierObservation.Latest(hook.AltObservation, raw.AltObservation));
        Assert.False(Held(hook.AltObservation, raw.AltObservation));
    }

    [Fact]
    public void QueriedDownStillWorksBeforeObserversReceiveThePress()
    {
        var release = OverlayModifierObservation.Capture(false);
        Assert.True(OverlayInteraction.ResolveHeld(
            OverlayModifierObservation.Latest(release, OverlayModifierObservation.Unknown), queried: true));
    }

    private static bool Held(OverlayModifierObservation first, OverlayModifierObservation second) =>
        OverlayInteraction.ResolveHeld(OverlayModifierObservation.Latest(first, second), queried: false);
}
