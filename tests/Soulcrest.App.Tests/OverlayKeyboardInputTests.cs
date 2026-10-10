using System.Runtime.InteropServices;
using Soulcrest.App.Overlay;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayKeyboardInputTests
{
    [Fact]
    public void UnobservedModifiersRemainUnknownAndOtherKeysAreDiscarded()
    {
        var input = new OverlayKeyboardInput();
        Assert.Null(input.AltHeld);
        Assert.Null(input.ShiftHeld);
        Assert.False(input.ProcessKeyboard(1, 0x41, 0x1e, 0));
        Assert.False(input.ProcessKeyboard(1, 0xff, 0, 0));
        Assert.False(input.ProcessKeyboard(1, OverlayKeyboardInput.AltKey, 0x38, OverlayKeyboardInput.KeyE1));
        Assert.False(input.ProcessKeyboard(1, OverlayKeyboardInput.ShiftKey, 0x00, 0));
        Assert.Null(input.AltHeld);
        Assert.Null(input.ShiftHeld);
    }

    [Fact]
    public void AltMakeBreakIsAuthoritativeAndPreservesTheOtherSide()
    {
        var input = new OverlayKeyboardInput();
        input.ProcessKeyboard(1, OverlayKeyboardInput.AltKey, 0x38, 0);
        Assert.True(input.AltHeld);
        Assert.Null(input.ShiftHeld);
        input.ProcessKeyboard(1, OverlayKeyboardInput.AltKey, 0x38, OverlayKeyboardInput.KeyE0);
        input.ProcessKeyboard(1, OverlayKeyboardInput.AltKey, 0x38, OverlayKeyboardInput.KeyBreak);
        Assert.True(input.AltHeld); // Right Alt is still held.
        input.ProcessKeyboard(1, OverlayKeyboardInput.AltKey, 0x38,
            OverlayKeyboardInput.KeyBreak | OverlayKeyboardInput.KeyE0);
        Assert.False(input.AltHeld);
        Assert.Null(input.ShiftHeld);
    }

    [Theory]
    [InlineData(OverlayKeyboardInput.LeftAltKey, 0x38, OverlayKeyboardInput.KeyE0)]
    [InlineData(OverlayKeyboardInput.RightAltKey, 0x38, 0)]
    public void ExplicitAltVirtualKeysTakePriorityOverTheExtendedFlag(ushort key, ushort scan, ushort flags)
    {
        var input = new OverlayKeyboardInput();
        input.ProcessKeyboard(1, key, scan, flags);
        Assert.True(input.AltHeld);
        input.ProcessKeyboard(1, key, scan, (ushort)(flags | OverlayKeyboardInput.KeyBreak));
        Assert.False(input.AltHeld);
    }

    [Fact]
    public void ShiftUsesTheScanCodeAndAltGrControlDoesNotChangeItsState()
    {
        var input = new OverlayKeyboardInput();
        input.ProcessKeyboard(1, OverlayKeyboardInput.ShiftKey, 0x2a, 0);
        input.ProcessKeyboard(1, OverlayKeyboardInput.ShiftKey, 0x36, 0);
        input.ProcessKeyboard(1, OverlayKeyboardInput.ShiftKey, 0x2a, OverlayKeyboardInput.KeyBreak);
        Assert.True(input.ShiftHeld);
        input.ProcessKeyboard(1, OverlayKeyboardInput.ShiftKey, 0x36, OverlayKeyboardInput.KeyBreak);
        Assert.False(input.ShiftHeld);
        Assert.False(input.ProcessKeyboard(1, 0x11, 0x1d, 0)); // AltGr's accompanying Control.
        Assert.Null(input.AltHeld);
        input.ProcessKeyboard(1, OverlayKeyboardInput.AltKey, 0x38, OverlayKeyboardInput.KeyE0);
        Assert.True(input.AltHeld);
        Assert.False(input.ShiftHeld);
    }

    [Theory]
    [InlineData(OverlayKeyboardInput.LeftShiftKey)]
    [InlineData(OverlayKeyboardInput.RightShiftKey)]
    public void ExplicitShiftVirtualKeysNeedNoScanCode(ushort key)
    {
        var input = new OverlayKeyboardInput();
        input.ProcessKeyboard(1, key, 0, 0);
        Assert.True(input.ShiftHeld);
        input.ProcessKeyboard(1, key, 0, OverlayKeyboardInput.KeyBreak);
        Assert.False(input.ShiftHeld);
        Assert.Null(input.AltHeld);
    }

    [Fact]
    public void RepeatsDoNotRequireMatchingReleaseCountsAndDevicesRemainIndependent()
    {
        var input = new OverlayKeyboardInput();
        input.ProcessKeyboard(1, OverlayKeyboardInput.LeftAltKey, 0x38, 0);
        input.ProcessKeyboard(1, OverlayKeyboardInput.LeftAltKey, 0x38, 0);
        input.ProcessKeyboard(2, OverlayKeyboardInput.LeftAltKey, 0x38, 0);
        input.ProcessKeyboard(1, OverlayKeyboardInput.LeftAltKey, 0x38, OverlayKeyboardInput.KeyBreak);
        Assert.True(input.AltHeld);
        input.ProcessKeyboard(3, OverlayKeyboardInput.LeftAltKey, 0x38, OverlayKeyboardInput.KeyBreak);
        Assert.True(input.AltHeld); // A release from another keyboard cannot unlock this one.
        input.ProcessKeyboard(2, OverlayKeyboardInput.LeftAltKey, 0x38, OverlayKeyboardInput.KeyBreak);
        Assert.False(input.AltHeld);
    }

    [Fact]
    public void RemovingOneKeyboardReleasesOnlyItsModifiers()
    {
        var input = new OverlayKeyboardInput();
        input.ProcessKeyboard(1, OverlayKeyboardInput.LeftAltKey, 0x38, 0);
        input.ProcessKeyboard(2, OverlayKeyboardInput.RightAltKey, 0x38, 0);
        input.ProcessKeyboard(2, OverlayKeyboardInput.LeftShiftKey, 0x2a, 0);
        input.RemoveDevice(1);
        Assert.True(input.AltHeld);
        Assert.True(input.ShiftHeld);
        input.RemoveDevice(2);
        Assert.False(input.AltHeld);
        Assert.False(input.ShiftHeld);
        input.RemoveDevice(99);
        Assert.False(input.AltHeld);
    }

    [Fact]
    public void AFirstReleaseIsKnownUpAndStopClearsTheStateWithoutARegistration()
    {
        var input = new OverlayKeyboardInput();
        input.ProcessKeyboard(0, OverlayKeyboardInput.LeftAltKey, 0x38, OverlayKeyboardInput.KeyBreak);
        Assert.False(input.AltHeld);
        input.ProcessKeyboard(0, OverlayKeyboardInput.RightShiftKey, 0x36, 0);
        Assert.True(input.ShiftHeld);
        input.Stop();
        Assert.Null(input.AltHeld);
        Assert.Null(input.ShiftHeld);
        input.Stop();
        Assert.False(input.Start(0));
        Assert.Null(input.AltHeld);
        Assert.Null(input.ShiftHeld);
        input.ProcessMessage(OverlayKeyboardInput.InputMessage, 1); // No registration: do not dereference.
        Assert.Null(input.AltHeld);
    }

    [Fact]
    public void NativeKeyboardLayoutAndCompletePacketAreAccepted()
    {
        Assert.Equal(IntPtr.Size == 8 ? 24 : 16, Marshal.SizeOf<OverlayRawInputHeader>());
        Assert.Equal(16, Marshal.SizeOf<OverlayRawKeyboard>());
        Assert.Equal(IntPtr.Size == 8 ? 40 : 32, Marshal.SizeOf<OverlayRawKeyboardInput>());
        Assert.Equal(Marshal.SizeOf<OverlayRawInputHeader>(),
            Marshal.OffsetOf<OverlayRawKeyboardInput>(nameof(OverlayRawKeyboardInput.Keyboard)).ToInt32());
        var input = new OverlayKeyboardInput();
        var packet = Packet();
        Assert.True(input.ProcessPacket(packet, packet.Header.Size, packet.Header.Size));
        Assert.True(input.AltHeld);
        Assert.Null(input.ShiftHeld);
    }

    [Theory]
    [InlineData(0, 0, 0)] // A compact keyboard packet (40 bytes on win-x64).
    [InlineData(8, 8, 8)] // A native RAWINPUT union-sized packet (48 bytes on win-x64).
    [InlineData(0, 0, 8)] // The supplied capacity can include unused padding.
    [InlineData(0, 8, 8)] // Bytes after the declared keyboard packet are harmless padding.
    public void NativeReaderQueriesTheRequiredSizeAndAcceptsCompletePaddedKeyboardPackets(
        uint headerPadding, uint copiedPadding, uint capacityPadding)
    {
        var input = new OverlayKeyboardInput();
        var packet = Packet();
        var minimum = packet.Header.Size;
        var nativeSize = minimum + capacityPadding;
        packet.Header.Size += headerPadding;
        var calls = 0;

        uint Read(nint handle, uint command, nint buffer, ref uint size, uint headerSize)
        {
            Assert.Equal((nint)123, handle);
            Assert.Equal(0x10000003u, command);
            Assert.Equal((uint)Marshal.SizeOf<OverlayRawInputHeader>(), headerSize);
            calls++;
            if (buffer == 0)
            {
                Assert.Equal(1, calls);
                Assert.Equal(0u, size);
                size = nativeSize;
                return 0;
            }

            Assert.Equal(2, calls);
            // This is Windows' failure for the former fixed 40-byte destination when 48 are required.
            if (size < nativeSize)
            {
                size = nativeSize;
                Marshal.SetLastPInvokeError(122); // ERROR_INSUFFICIENT_BUFFER
                return uint.MaxValue;
            }
            Assert.Equal(nativeSize, size);
            Marshal.StructureToPtr(packet, buffer, false);
            for (var offset = (int)minimum; offset < nativeSize; offset++)
                Marshal.WriteByte(buffer, offset, 0x5a);
            return minimum + copiedPadding;
        }

        Assert.True(input.ReadPacket(123, Read));
        Assert.Equal(2, calls);
        Assert.True(input.AltHeld);
        Assert.Null(input.ShiftHeld);
    }

    [Theory]
    [InlineData(uint.MaxValue, 48)]
    [InlineData(1, 48)] // A NULL-buffer query must return zero on success.
    [InlineData(0, 0)]
    [InlineData(0, 39)]
    [InlineData(0, 1025)]
    public void FailedOrUnreasonableSizeQueriesNeverAllocateOrChangeModifiers(uint result, uint requiredSize)
    {
        var input = new OverlayKeyboardInput();
        input.ProcessKeyboard(1, OverlayKeyboardInput.LeftAltKey, 0x38, 0);
        var calls = 0;
        uint Read(nint handle, uint command, nint buffer, ref uint size, uint headerSize)
        {
            calls++;
            Assert.Equal((nint)0, buffer);
            var delta = (uint)(40 - Marshal.SizeOf<OverlayRawKeyboardInput>());
            size = requiredSize == 39 ? requiredSize - delta : requiredSize;
            return result;
        }

        Assert.False(input.ReadPacket(123, Read));
        Assert.Equal(1, calls);
        Assert.True(input.AltHeld);
        Assert.Null(input.ShiftHeld);
    }

    [Theory]
    [InlineData(uint.MaxValue, 48, 122)] // A failed read must not parse the buffer, even if it resembles Alt.
    [InlineData(39, 40, 0)]
    [InlineData(41, 40, 0)]
    [InlineData(40, 39, 0)]
    [InlineData(40, 48, 0)] // Windows must not claim capacity beyond the allocation.
    public void FailedShortOrInconsistentNativeCopiesNeverChangeModifiers(uint copied, uint returnedSize, int error)
    {
        var input = new OverlayKeyboardInput();
        var packet = Packet();
        var delta = (uint)(40 - Marshal.SizeOf<OverlayRawKeyboardInput>());
        var calls = 0;
        uint Read(nint handle, uint command, nint buffer, ref uint size, uint headerSize)
        {
            calls++;
            if (buffer == 0)
            {
                size = packet.Header.Size;
                return 0;
            }
            Assert.Equal(packet.Header.Size, size);
            Marshal.StructureToPtr(packet, buffer, false);
            size = returnedSize - delta;
            Marshal.SetLastPInvokeError(error);
            return copied == uint.MaxValue ? copied : copied - delta;
        }

        Assert.False(input.ReadPacket(123, Read));
        Assert.Equal(2, calls);
        Assert.Null(input.AltHeld);
        Assert.Null(input.ShiftHeld);
    }

    [Theory]
    [InlineData(0, 40)]
    [InlineData(2, 40)]
    [InlineData(1, 39)]
    [InlineData(1, 49)]
    public void NativeReaderRejectsWrongTypesAndInvalidDeclaredSizes(uint type, uint declaredSize)
    {
        var input = new OverlayKeyboardInput();
        var packet = Packet();
        var delta = (uint)(40 - Marshal.SizeOf<OverlayRawKeyboardInput>());
        packet.Header.Type = type;
        packet.Header.Size = declaredSize - delta;
        uint Read(nint handle, uint command, nint buffer, ref uint size, uint headerSize)
        {
            if (buffer == 0) { size = 48 - delta; return 0; }
            Marshal.StructureToPtr(packet, buffer, false);
            return size;
        }

        Assert.False(input.ReadPacket(123, Read));
        Assert.Null(input.AltHeld);
        Assert.Null(input.ShiftHeld);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(0, 40, 40, 40)] // Mouse packet, even if the bytes resemble Alt.
    [InlineData(2, 40, 40, 40)] // HID packet.
    [InlineData(1, 39, 40, 40)]
    [InlineData(1, 40, 39, 40)]
    [InlineData(1, 40, 40, 39)]
    [InlineData(1, 41, 40, 40)]
    [InlineData(1, 40, 41, 40)]
    [InlineData(1, 40, 40, uint.MaxValue)]
    [InlineData(1, 40, uint.MaxValue, 40)]
    public void IncompleteWrongTypeAndFailedPacketsNeverChangeModifiers(uint type, uint headerSize, uint copied, uint size)
    {
        var input = new OverlayKeyboardInput();
        var packet = Packet();
        // Test cases describe the win-x64 native wire layout, adjusted for a possible x86 test runner.
        var delta = (uint)(40 - Marshal.SizeOf<OverlayRawKeyboardInput>());
        packet.Header.Type = type;
        packet.Header.Size = headerSize >= delta ? headerSize - delta : headerSize;
        copied = copied >= delta && copied != uint.MaxValue ? copied - delta : copied;
        size = size >= delta ? size - delta : size;
        Assert.False(input.ProcessPacket(packet, copied, size));
        Assert.Null(input.AltHeld);
        Assert.Null(input.ShiftHeld);
    }

    private static OverlayRawKeyboardInput Packet() => new()
    {
        Header = new OverlayRawInputHeader
        {
            Type = 1, Size = (uint)Marshal.SizeOf<OverlayRawKeyboardInput>(), Device = 1
        },
        Keyboard = new OverlayRawKeyboard { VirtualKey = OverlayKeyboardInput.AltKey, MakeCode = 0x38 }
    };
}
