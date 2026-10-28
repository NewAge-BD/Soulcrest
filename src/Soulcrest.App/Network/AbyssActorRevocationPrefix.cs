namespace Soulcrest.App.Network;

/// <summary>
/// Revocation-only actor prefix. NPC forms retain their independently bridged prefix; the narrow
/// CharDisappear form is supported by the shared ActionProtocol_RS key and closed actor corpus.
/// Child bytes remain opaque. This can only destroy PC evidence, never create or rearm it.
/// Unsupported shapes or child-byte forms require the caller's global invalidation fallback.
/// </summary>
internal static class AbyssActorRevocationPrefix
{
    internal static int? TryRead(ReadOnlySpan<byte> message)
    {
        if (message.Length < 3 || message[1] != 0x36 || message[0] is not (0x41 or 0x42 or 0x47)) return null;
        var charDisappear = message[0] == 0x47;
        // Only the independently compared one/two-byte actor keys plus two opaque child bytes.
        // Longer keys/bodies have no current corpus qualification for this opcode.
        if (charDisappear && message.Length is not (5 or 6)) return null;
        uint value = 0;
        for (var i = 0; i < 5 && i + 2 < message.Length; i++)
        {
            var next = message[i + 2];
            if (i == 4 && next > 15) return null;
            value |= (uint)(next & 127) << (i * 7);
            if (next >= 128) continue;
            if (i > 0 && next == 0) return null;
            if (value is not (> 0 and <= int.MaxValue)) return null;
            if (charDisappear && !QualifiedCharDisappearTail(message[(i + 3)..], i + 1)) return null;
            return (int)value;
        }
        return null;
    }

    // Empirical child-byte allowlist, without claiming reason/effect or presence-mask semantics.
    // A new pair cannot silently narrow a previously global revocation.
    private static bool QualifiedCharDisappearTail(ReadOnlySpan<byte> tail, int keyBytes)
    {
        if (tail.Length != 2) return false;
        if (keyBytes == 1) return tail[0] == 0x01 && tail[1] == 0x00;
        return keyBytes == 2 && (tail[0], tail[1]) is
            (0x01, 0x00) or (0x0A, 0x4C) or (0x0A, 0x00) or (0x02, 0x4F)
            or (0x0A, 0x46) or (0x04, 0x4F) or (0x02, 0x1E);
    }
}
