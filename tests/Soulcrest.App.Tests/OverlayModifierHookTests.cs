using Soulcrest.App.Overlay;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayModifierHookTests
{
    [Fact]
    public void NativeObserverStartsAndRestartsItsOwnMessagePumpWithoutSendingInput()
    {
        using var hook = new OverlayModifierHook();
        Assert.True(hook.Start());
        Assert.True(hook.Start());
        hook.Stop();
        Assert.Null(hook.AltHeld);
        Assert.Null(hook.ShiftHeld);
        Assert.True(hook.Start());
        hook.Stop();
    }

    [Fact]
    public void ObserverRetainsOnlyAltAndShiftAndIgnoresUnknownMessages()
    {
        using var hook = new OverlayModifierHook();
        var changes = 0;
        hook.Changed += () => changes++;
        Assert.Null(hook.AltHeld);
        Assert.Null(hook.ShiftHeld);
        Assert.False(hook.ProcessTransition(OverlayModifierHook.KeyDown, 0x41, 0x1e, 0));
        Assert.False(hook.ProcessTransition(OverlayModifierHook.KeyUp, 0xff, 0, 0));
        Assert.False(hook.ProcessTransition(0x0201, OverlayModifierHook.LeftAltKey, 0x38, 0));
        Assert.False(hook.ProcessTransition(OverlayModifierHook.KeyDown, OverlayModifierHook.ShiftKey, 0, 0));
        Assert.Null(hook.AltHeld);
        Assert.Null(hook.ShiftHeld);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void BothAltSidesRemainHeldUntilTheLastReleaseAndRepeatsDoNotToggle()
    {
        using var hook = new OverlayModifierHook();
        var changes = 0;
        hook.Changed += () => changes++;
        Assert.True(hook.ProcessTransition(OverlayModifierHook.SystemKeyDown, OverlayModifierHook.LeftAltKey, 0x38, 0));
        Assert.True(hook.AltHeld);
        hook.ProcessTransition(OverlayModifierHook.SystemKeyDown, OverlayModifierHook.LeftAltKey, 0x38, 0);
        hook.ProcessTransition(OverlayModifierHook.SystemKeyDown, OverlayModifierHook.RightAltKey, 0x38, OverlayModifierHook.ExtendedKey);
        hook.ProcessTransition(OverlayModifierHook.SystemKeyUp, OverlayModifierHook.LeftAltKey, 0x38, 0);
        Assert.True(hook.AltHeld);
        Assert.Equal(1, changes);
        hook.ProcessTransition(OverlayModifierHook.SystemKeyUp, OverlayModifierHook.RightAltKey, 0x38, OverlayModifierHook.ExtendedKey);
        Assert.False(hook.AltHeld);
        Assert.Null(hook.ShiftHeld);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void GenericAltUsesTheLowLevelExtendedFlagAndShiftUsesItsScanCode()
    {
        using var hook = new OverlayModifierHook();
        hook.ProcessTransition(OverlayModifierHook.KeyDown, OverlayModifierHook.AltKey, 0x38, 0);
        hook.ProcessTransition(OverlayModifierHook.KeyDown, OverlayModifierHook.AltKey, 0x38, OverlayModifierHook.ExtendedKey);
        hook.ProcessTransition(OverlayModifierHook.KeyUp, OverlayModifierHook.AltKey, 0x38, 0);
        Assert.True(hook.AltHeld);
        hook.ProcessTransition(OverlayModifierHook.KeyUp, OverlayModifierHook.AltKey, 0x38, OverlayModifierHook.ExtendedKey);
        Assert.False(hook.AltHeld);
        hook.ProcessTransition(OverlayModifierHook.KeyDown, OverlayModifierHook.ShiftKey, 0x2a, 0);
        hook.ProcessTransition(OverlayModifierHook.KeyDown, OverlayModifierHook.ShiftKey, 0x36, 0);
        hook.ProcessTransition(OverlayModifierHook.KeyUp, OverlayModifierHook.ShiftKey, 0x2a, 0);
        Assert.True(hook.ShiftHeld);
        hook.ProcessTransition(OverlayModifierHook.KeyUp, OverlayModifierHook.ShiftKey, 0x36, 0);
        Assert.False(hook.ShiftHeld);
    }

    [Theory]
    [InlineData(OverlayModifierHook.LeftAltKey, 0u, OverlayModifierHook.ExtendedKey)]
    [InlineData(OverlayModifierHook.RightAltKey, 0u, 0u)]
    [InlineData(OverlayModifierHook.LeftShiftKey, 0u, 0u)]
    [InlineData(OverlayModifierHook.RightShiftKey, 0u, 0u)]
    public void ExplicitSideKeysTakePriorityOverScanCodeAndFlags(uint key, uint scanCode, uint flags)
    {
        using var hook = new OverlayModifierHook();
        hook.ProcessTransition(OverlayModifierHook.KeyDown, key, scanCode, flags);
        var alt = key is OverlayModifierHook.LeftAltKey or OverlayModifierHook.RightAltKey;
        Assert.True(alt ? hook.AltHeld : hook.ShiftHeld);
        Assert.Null(alt ? hook.ShiftHeld : hook.AltHeld);
        hook.ProcessTransition(OverlayModifierHook.KeyUp, key, scanCode, flags);
        Assert.False(alt ? hook.AltHeld : hook.ShiftHeld);
    }

    [Fact]
    public void AltGrControlDoesNotRetainAnotherKeyOrChangeShift()
    {
        using var hook = new OverlayModifierHook();
        Assert.False(hook.ProcessTransition(OverlayModifierHook.KeyDown, 0xa2, 0x1d, 0));
        hook.ProcessTransition(OverlayModifierHook.SystemKeyDown, OverlayModifierHook.RightAltKey, 0x38, OverlayModifierHook.ExtendedKey);
        Assert.True(hook.AltHeld);
        Assert.Null(hook.ShiftHeld);
        Assert.False(hook.ProcessTransition(OverlayModifierHook.KeyUp, 0xa2, 0x1d, 0));
        Assert.True(hook.AltHeld);
        hook.ProcessTransition(OverlayModifierHook.SystemKeyUp, OverlayModifierHook.RightAltKey, 0x38, OverlayModifierHook.ExtendedKey);
        Assert.False(hook.AltHeld);
    }

    [Fact]
    public void FirstReleaseEstablishesKnownUpAndStopForgetsTheState()
    {
        using var hook = new OverlayModifierHook();
        var changes = 0;
        hook.Changed += () => changes++;
        hook.ProcessTransition(OverlayModifierHook.KeyUp, OverlayModifierHook.LeftAltKey, 0x38, 0);
        Assert.False(hook.AltHeld);
        Assert.Null(hook.ShiftHeld);
        Assert.Equal(1, changes);
        hook.Stop();
        Assert.Null(hook.AltHeld);
        Assert.Null(hook.ShiftHeld);
        Assert.Equal(2, changes);
        hook.Stop();
        Assert.Equal(2, changes);
    }

    [Fact]
    public void InputSourceFlagsDoNotBlockModifiersAndMessageDeterminesTheTransition()
    {
        using var hook = new OverlayModifierHook();
        // LLKHF_INJECTED/LLKHF_UP are observations only. WM_KEYDOWN/UP determine the state.
        hook.ProcessTransition(OverlayModifierHook.KeyDown, OverlayModifierHook.LeftAltKey, 0x38, 0x90);
        Assert.True(hook.AltHeld);
        hook.ProcessTransition(OverlayModifierHook.KeyUp, OverlayModifierHook.LeftAltKey, 0x38, 0x10);
        Assert.False(hook.AltHeld);
    }

    [Fact]
    public void FailingSubscriberCannotEscapeTheObserverOrPreventRelease()
    {
        using var hook = new OverlayModifierHook();
        hook.Changed += () => throw new InvalidOperationException("Subscriber closing");
        Assert.True(hook.ProcessTransition(OverlayModifierHook.KeyDown, OverlayModifierHook.LeftAltKey, 0x38, 0));
        Assert.True(hook.AltHeld);
        Assert.True(hook.ProcessTransition(OverlayModifierHook.KeyUp, OverlayModifierHook.LeftAltKey, 0x38, 0));
        Assert.False(hook.AltHeld);
        hook.Stop();
        Assert.Null(hook.AltHeld);
    }

    [Fact]
    public void DisposalIsIdempotentAndCannotStartANativeObserverAgain()
    {
        var hook = new OverlayModifierHook();
        hook.ProcessTransition(OverlayModifierHook.KeyDown, OverlayModifierHook.LeftShiftKey, 0x2a, 0);
        Assert.True(hook.ShiftHeld);
        hook.Dispose();
        hook.Dispose();
        Assert.Null(hook.AltHeld);
        Assert.Null(hook.ShiftHeld);
        Assert.False(hook.Start());
    }
}
