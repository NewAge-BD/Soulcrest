using System.Buffers.Binary;
using Soulcrest.App.Overlay;

namespace Soulcrest.App.Services;

/// <summary>Original restrained harp/string stems, aligned to the same choreography as the icon.</summary>
internal static class AbyssRankSound
{
    internal const int SampleRate = 48000;
    internal const int Channels = 2;
    internal const int CacheCapacity = 8;
    // User feedback: retain the independent volume setting, but soften the orchestration by 11 dB (another 3 dB after the first adjustment).
    private const float MusicalGain = .28183829f;
    private static readonly object Gate = new();
    private static readonly LinkedList<(int Steps, bool Reduced, byte[] Wave)> Cache = new();
    private static readonly Lazy<Stems> Source = new(LoadStems);

    // Points only select a catalog route; no profile, name or user audio is retained.
    internal static byte[] CreateWave(long fromPoints, long toPoints, bool reduced)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromPoints);
        ArgumentOutOfRangeException.ThrowIfNegative(toPoints);
        var stages = AbyssRankCatalog.Default.Stages;
        var from = Index(fromPoints);
        var to = Index(toPoints);
        var steps = to - from;
        if (steps <= 0) return [];
        lock (Gate)
        {
            var node = Cache.First;
            while (node is not null)
            {
                if (node.Value.Steps == steps && node.Value.Reduced == reduced)
                {
                    Cache.Remove(node); Cache.AddFirst(node);
                    return node.Value.Wave;
                }
                node = node.Next;
            }
            var morph = AbyssRankMotionTimeline.GetMorphStartMs(fromPoints, toPoints, reduced) / 1000;
            var landing = AbyssRankMotionTimeline.GetCommitMs(fromPoints, toPoints, reduced) / 1000;
            var stems = Source.Value;
            var frames = (int)Math.Ceiling((landing + stems.Land.Length / (double)(SampleRate * Channels)) * SampleRate);
            var mix = new float[frames * Channels];
            if (!reduced)
            {
                Add(mix, stems.Rise, 0, .82f);
                var riseDuration = stems.Rise.Length / (double)(SampleRate * Channels);
                // Preserve harp pitch while a quiet existing pad joins the slower travel to its bloom.
                if (morph > riseDuration)
                    Sustain(mix, stems.Sustain, Math.Max(0, riseDuration - .16), morph + .08, .08f);
            }
            Add(mix, stems.Bloom, morph, reduced ? .44f : .8f);
            // One soft evolution accent per whole route. A quiet string bed carries long routes.
            if (landing - morph > .8)
                Sustain(mix, stems.Sustain, morph + .28, landing, reduced ? .22f : .34f);
            Add(mix, stems.Land, landing, reduced ? .56f : .8f);
            var wave = Encode(mix);
            Cache.AddFirst((steps, reduced, wave));
            if (Cache.Count > CacheCapacity) Cache.RemoveLast();
            return wave;
        }

        int Index(long points)
        {
            for (var index = stages.Count - 1; index >= 0; index--)
                if (points >= stages[index].Threshold) return index;
            return 0;
        }
    }

    private static void Add(float[] target, float[] stem, double startSeconds, float gain)
    {
        var offset = (int)Math.Round(startSeconds * SampleRate) * Channels;
        for (var index = 0; index < stem.Length && offset + index < target.Length; index++)
            target[offset + index] += stem[index] * gain;
    }

    private static void Sustain(float[] target, float[] stem, double startSeconds, double endSeconds, float gain)
    {
        var start = (int)Math.Round(startSeconds * SampleRate);
        var end = (int)Math.Round(endSeconds * SampleRate);
        var loopFrames = stem.Length / Channels;
        const int overlap = SampleRate / 10;
        var stride = loopFrames - overlap;
        for (var origin = start; origin < end; origin += stride)
            for (var frame = 0; frame < loopFrames && origin + frame < end; frame++)
            {
                var fadeIn = Math.Min(1, frame / (double)overlap);
                var fadeOut = Math.Min(1, (loopFrames - 1 - frame) / (double)overlap);
                var routeFade = Math.Min(1, (end - 1 - origin - frame) / (double)(SampleRate / 5));
                var envelope = (float)(fadeIn * fadeOut * routeFade) * gain;
                for (var channel = 0; channel < Channels; channel++)
                    target[(origin + frame) * Channels + channel] += stem[frame * Channels + channel] * envelope;
            }
    }

    private static byte[] Encode(float[] mix)
    {
        var peak = mix.Max(sample => Math.Abs(sample));
        var gain = (peak > .88f ? .88f / peak : 1) * MusicalGain;
        var result = new byte[44 + mix.Length * sizeof(short)];
        "RIFF"u8.CopyTo(result); BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), result.Length - 8);
        "WAVEfmt "u8.CopyTo(result.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(22), Channels);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(24), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(28), SampleRate * Channels * sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(32), Channels * sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(34), 16);
        "data"u8.CopyTo(result.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(40), mix.Length * sizeof(short));
        const int fadeFrames = SampleRate / 100;
        for (var index = 0; index < mix.Length; index++)
        {
            var frame = index / Channels;
            var edge = Math.Min(1, Math.Min(frame, mix.Length / Channels - 1 - frame) / (double)fadeFrames);
            var value = (short)Math.Round(Math.Clamp(mix[index] * gain * edge, -.88, .88) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(44 + index * sizeof(short)), value);
        }
        return result;
    }

    private static Stems LoadStems() => new(Read("rise"), Read("bloom"), Read("sustain"), Read("land"));

    private static float[] Read(string name)
    {
        using var stream = typeof(AbyssRankSound).Assembly.GetManifestResourceStream($"Soulcrest.AbyssRankAudio.{name}.wav")
            ?? throw new InvalidDataException($"Missing original Abyss rank audio stem: {name}.");
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != 0x46464952) throw new InvalidDataException("Abyss rank audio is not RIFF.");
        _ = reader.ReadUInt32();
        if (reader.ReadUInt32() != 0x45564157) throw new InvalidDataException("Abyss rank audio is not WAVE.");
        var validFormat = false;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = reader.ReadUInt32();
            var length = reader.ReadInt32();
            if (length < 0 || length > stream.Length - stream.Position) throw new InvalidDataException("Truncated Abyss rank audio.");
            var next = stream.Position + length + (length & 1);
            if (id == 0x20746d66)
            {
                if (length < 16) throw new InvalidDataException("Invalid Abyss rank audio format.");
                validFormat = reader.ReadInt16() == 1 && reader.ReadInt16() == Channels && reader.ReadInt32() == SampleRate;
                _ = reader.ReadInt32(); _ = reader.ReadInt16();
                validFormat &= reader.ReadInt16() == 16;
            }
            else if (id == 0x61746164)
            {
                if (!validFormat || length % (Channels * sizeof(short)) != 0 || length < 960)
                    throw new InvalidDataException("Abyss rank audio must be stereo 48 kHz PCM16.");
                var samples = new float[length / sizeof(short)];
                for (var index = 0; index < samples.Length; index++) samples[index] = reader.ReadInt16() / 32768f;
                return samples;
            }
            stream.Position = next;
        }
        throw new InvalidDataException("Missing Abyss rank audio samples.");
    }

    private sealed record Stems(float[] Rise, float[] Bloom, float[] Sustain, float[] Land);
}
