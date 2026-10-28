using System.Buffers.Binary;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class AbyssRankSoundTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryAscendingRouteIsStereoPcmAndFollowsItsOwnLanding(bool reduced)
    {
        var stages = AbyssRankCatalog.Default.Stages;
        for (var steps = 1; steps < stages.Count; steps++)
        {
            var from = stages[0].Threshold;
            var to = stages[steps].Threshold;
            var wave = AbyssRankSound.CreateWave(from, to, reduced);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wave, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wave, 8, 4));
            Assert.Equal(wave.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(4)));
            Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(22)));
            Assert.Equal(48000, BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(24)));
            Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(34)));
            var seconds = (wave.Length - 44) / (48000d * 4);
            var landing = AbyssRankMotionTimeline.GetCommitMs(from, to, reduced) / 1000;
            Assert.Equal(landing + 1, seconds, 4);
            Assert.InRange(seconds, 1.5, 7.07);
            Assert.Equal(0, Sample(wave, 0));
            Assert.Equal(0, Sample(wave, (wave.Length - 44) / 2 - 1));
            var peak = 0;
            var energy = 0d;
            for (var sample = 0; sample < (wave.Length - 44) / 2; sample++)
            {
                var value = Sample(wave, sample);
                peak = Math.Max(peak, Math.Abs((int)value));
                energy += (double)value * value;
            }
            Assert.InRange(peak, 200, 2500);
            Assert.InRange(Math.Sqrt(energy / ((wave.Length - 44) / 2)) / short.MaxValue, .0005, .035);
            Assert.True(Energy(wave, landing + .05, landing + .25) > .000001, "The gentle landing must retain its audible resolution.");
        }
    }

    [Fact]
    public void SameRouteCachesIndependentOfAbsolutePointValuesAndRemainsDeterministic()
    {
        var stages = AbyssRankCatalog.Default.Stages;
        var original = AbyssRankSound.CreateWave(0, stages[1].Threshold, false);
        Assert.Same(original, AbyssRankSound.CreateWave(3, stages[1].Threshold + 3, false));
        for (var step = 2; step < stages.Count; step++)
            _ = AbyssRankSound.CreateWave(0, stages[step].Threshold, false);
        Assert.Equal(original, AbyssRankSound.CreateWave(0, stages[1].Threshold, false));
    }

    [Fact]
    public void ReducedCueIsQuieterAndHasNoTravelLeadIn()
    {
        var next = AbyssRankCatalog.Default.Stages[1].Threshold;
        var full = AbyssRankSound.CreateWave(0, next, false);
        var reduced = AbyssRankSound.CreateWave(0, next, true);
        Assert.True(Energy(reduced, 0, 1.8) < Energy(full, 0, 1.8) * .8);
        Assert.True(Energy(reduced, .05, .15) > .000001);
        Assert.True(reduced.Length < full.Length);
    }

    [Fact]
    public void BaselinesSameStageAndDescentsHaveNoCue()
    {
        var stages = AbyssRankCatalog.Default.Stages;
        Assert.Empty(AbyssRankSound.CreateWave(0, 1, false));
        Assert.Empty(AbyssRankSound.CreateWave(stages[1].Threshold, 0, false));
        Assert.Empty(AbyssRankSound.CreateWave(long.MaxValue, long.MaxValue, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => AbyssRankSound.CreateWave(-1, 0, false));
    }

    [Fact]
    public void LandingRetainsTheRequestedElevenDecibelMusicalAttenuation()
    {
        var highest = AbyssRankCatalog.Default.Stages[^1].Threshold;
        var wave = AbyssRankSound.CreateWave(0, highest, false);
        var landing = AbyssRankMotionTimeline.GetCommitMs(0, highest) / 1000;
        using var source = typeof(AbyssRankSoundPlayer).Assembly.GetManifestResourceStream("Soulcrest.AbyssRankAudio.land.wav");
        Assert.NotNull(source);
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        var stem = buffer.ToArray();
        var arrangedStemEnergy = Energy(stem, .1, .5) * .8 * .8;
        var measuredDb = 10 * Math.Log10(Energy(wave, landing + .1, landing + .5) / arrangedStemEnergy);
        Assert.InRange(measuredDb, -11.03, -10.97);
    }

    [Fact]
    public void SlowerTravelRetainsAQuietBridgeUntilTheMorphBegins()
    {
        var next = AbyssRankCatalog.Default.Stages[1].Threshold;
        var wave = AbyssRankSound.CreateWave(0, next, false);
        var morph = AbyssRankMotionTimeline.GetMorphStartMs(0, next) / 1000;
        Assert.True(Energy(wave, morph - .08, morph - .01) > .000000001);
    }

    private static short Sample(byte[] wave, int index) => BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(44 + index * 2));
    private static double Energy(byte[] wave, double start, double end)
    {
        var first = Math.Max(0, (int)(start * 48000) * 2);
        var last = Math.Min((wave.Length - 44) / 2, (int)(end * 48000) * 2);
        var sum = 0d;
        for (var sample = first; sample < last; sample++)
            sum += Math.Pow(Sample(wave, sample) / 32768d, 2);
        return sum / Math.Max(1, last - first);
    }
}
