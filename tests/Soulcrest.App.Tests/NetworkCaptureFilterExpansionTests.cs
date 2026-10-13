using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using System.Text;
using PacketDotNet;
using SharpPcap;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Live-handle lifecycle with a synthetic ILiveDevice; no Npcap, sockets or user files.</summary>
[Collection("Settings file")]
public sealed class NetworkCaptureFilterExpansionTests
{
    private static readonly CaptureConnection World = new(42, "192.0.2.2", 50000, "192.0.2.1", 9000);
    private static readonly CaptureConnection Web = new(42, "192.0.2.2", 50001, "198.51.100.1", 443);
    private static readonly CaptureConnection Added = new(42, "192.0.2.2", 50002, "203.0.113.1", 443);
    private static readonly OwnCharacterIdentity Own = new(101, 501, 300, "SyntheticFilterProfile");
    private static readonly DateTimeOffset At = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NewRemoteExpandsOnlyProvenCoverageWithoutInterruptingOwnOrAPartialWorldMessage()
    {
        using var f = new Fixture();
        f.Bind();
        var before = f.Network.ProgressSources;
        var generation = f.Generation;
        var original = f.Streams[World];
        var gain = Frame(Gain());
        f.Send(World, gain[..5]);
        var oldTarget = f.Target;
        var oldCoverage = f.Coverage;
        f.ConfigureNextHandle = handle => handle.BeforeFilter = _ =>
        {
            Assert.Same(oldTarget, f.Target);
            Assert.Same(oldCoverage, f.Coverage);
            Assert.Equal(Own.Name, f.Network.Character);
        };

        f.Network.UpdateCaptureTarget(Target(World, Added));
        f.Send(World, gain[5..]);

        Assert.Equal(10, f.Progress.Souls("fossa"));
        Assert.Equal(2, Assert.Single(f.Gains).Souls);
        Assert.Equal(Own.Name, f.Network.Character);
        Assert.Equal(before.PetBaselineAt, f.Network.PetBaselineAt);
        Assert.Equal(before.QuestBaselineAt, f.Network.QuestBaselineAt);
        Assert.Equal(before.TeleportBaselineAt, f.Network.TeleportBaselineAt);
        Assert.True(f.Network.ProgressSources.InitialProgressConfirmed);
        Assert.Equal(generation, f.Generation);
        Assert.Same(original.Tcp, f.Streams[World].Tcp);
        Assert.Same(original.Messages, f.Streams[World].Messages);
        Assert.True(f.Coverage.SetEquals(new[] { World.RemoteEndpoint, Web.RemoteEndpoint, Added.RemoteEndpoint }));
        Assert.Equal("tcp and ((host 203.0.113.1 and port 443))", Assert.Single(f.Extensions.Single().Calls.Filters));
        Assert.True(f.Extensions.Single().Calls.NativeCallsOutsideGate);
        Assert.Equal(1, f.Extensions.Single().Calls.Count("Open"));
        Assert.Equal(1, f.Extensions.Single().Calls.Count("StartCapture"));
        Assert.Equal(new[] { Web.StreamKey }, f.Gaps);
        Assert.Contains(f.Network.ProgressDiagnostics.Events, e => e.Kind == NetworkProgressEventKind.FilterExpanded);
        AssertHandleNeverRestarted(f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FilterFailureKeepsOldOwnershipAndCanRetryWithoutClosingTheCapture(bool invalidOperation)
    {
        using var f = new Fixture();
        f.Bind();
        var originalTarget = f.Target;
        var originalCoverage = f.Coverage;
        var originalStream = f.Streams[World];
        f.ConfigureNextHandle = handle => handle.FilterError = invalidOperation ? new InvalidOperationException("Synthetic filter failure") : PcapFailure();
        f.Network.UpdateCaptureTarget(Target(World, Web, Added));

        Assert.True(f.Network.Running);
        Assert.True(f.Network.StatusIsError);
        Assert.Same(originalTarget, f.Target);
        Assert.Same(originalCoverage, f.Coverage);
        Assert.Same(originalStream.Tcp, f.Streams[World].Tcp);
        Assert.Equal(Own.Name, f.Network.Character);
        Assert.True(f.Network.ProgressSources.InitialProgressConfirmed);
        Assert.Equal("Mitschnittfilter konnte nicht erweitert werden. Bestehende Spielverbindungen werden weiter gelesen; ein neuer Versuch folgt.", f.Network.Status);
        f.Send(Added, Frame(Gain()));
        Assert.Equal(8, f.Progress.Souls("fossa")); // Uncovered new ownership was not adopted.
        f.Send(World, Frame(Gain()));
        Assert.Equal(10, f.Progress.Souls("fossa"));
        AssertHandleNeverRestarted(f);

        Assert.Equal(1, f.Extensions.Single().Calls.Count("Close"));
        f.Network.UpdateCaptureTarget(Target(World, Web, Added));
        Assert.False(f.Network.StatusIsError);
        Assert.Equal(2, f.Extensions.Count);
        Assert.All(f.Extensions, handle => Assert.Single(handle.Calls.Filters));
        Assert.Contains(Added, f.Target.Connections!);
        Assert.Contains(Added.RemoteEndpoint, f.Coverage);
        Assert.Equal(new[] { NetworkProgressEventKind.FilterFailed, NetworkProgressEventKind.FilterExpanded },
            f.Network.ProgressDiagnostics.Events.Where(e => e.Kind is NetworkProgressEventKind.FilterFailed or NetworkProgressEventKind.FilterExpanded).Select(e => e.Kind));
        AssertHandleNeverRestarted(f);
    }

    [Fact]
    public void FailedExpansionStillRevokesAKnownRemovedWorldTupleAndRejectsItsQueuedData()
    {
        using var f = new Fixture();
        f.Bind();
        var coverage = f.Coverage;
        f.ConfigureNextHandle = handle => handle.FilterError = PcapFailure();
        f.Network.UpdateCaptureTarget(Target(Web, Added));
        Assert.True(f.Network.StatusIsError);
        Assert.Same(coverage, f.Coverage);
        Assert.True(f.Target.Connections!.SetEquals(new[] { Web }));
        Assert.False(f.Streams.ContainsKey(World));
        Assert.Null(f.Network.Character);
        Assert.False(f.Network.ProgressSources.HasAnyBaseline);
        Assert.Equal(new[] { World.StreamKey }, f.Gaps);
        f.Send(World, [.. Frame(Clock()), .. Frame(Gain())]);
        Assert.Equal(8, f.Progress.Souls("fossa"));
        Assert.Empty(f.Gains);
        Assert.False(f.Streams.ContainsKey(World));
        AssertHandleNeverRestarted(f);
    }

    [Fact]
    public void FailedExpansionWarningClearsWhenTheUncoveredEndpointDisappearsAgain()
    {
        using var f = new Fixture();
        f.Bind();
        f.ConfigureNextHandle = handle => handle.FilterError = PcapFailure();
        f.Network.UpdateCaptureTarget(Target(World, Web, Added));
        Assert.True(f.Network.StatusIsError);
        f.Network.UpdateCaptureTarget(Target(World, Web));
        Assert.False(f.Network.StatusIsError);
        Assert.Single(f.Extensions); // No retry is necessary for the unchanged installed coverage.
        Assert.Equal(Own.Name, f.Network.Character);
        Assert.True(f.Network.ProgressSources.InitialProgressConfirmed);
        AssertHandleNeverRestarted(f);
    }

    [Fact]
    public void ShrinkingAndReaddingCoveredEndpointsNeverShrinkOrRewriteTheInstalledFilter()
    {
        using var f = new Fixture();
        f.Bind();
        f.Network.UpdateCaptureTarget(Target(World, Web, Added));
        var expanded = f.Coverage;
        f.Network.UpdateCaptureTarget(Target(World));
        f.Network.UpdateCaptureTarget(Target(World, Web with { LocalPort = 50003 }, Added with { LocalPort = 50004 }));
        Assert.Single(f.Extensions);
        Assert.Single(f.Extensions[0].Calls.Filters);
        Assert.Same(expanded, f.Coverage);
        Assert.True(f.Network.ProgressSources.InitialProgressConfirmed);
        AssertHandleNeverRestarted(f);
    }

    [Fact]
    public void ARealDetectedGapAfterFilterExpansionStillInvalidatesOwnInsteadOfClaimingLosslessTransition()
    {
        using var f = new Fixture();
        f.Bind();
        f.Network.UpdateCaptureTarget(Target(World, Web, Added));
        f.Network.ProcessProgressGap(World.StreamKey);
        f.Send(World, Frame(Gain()));
        Assert.Null(f.Network.Character);
        Assert.False(f.Network.ProgressSources.HasAnyBaseline);
        Assert.Equal(10, f.Progress.Souls("fossa"));
        Assert.Equal(ProgressFreshness.Unknown, f.Progress.EntrySnapshot("fossa").Freshness);
        Assert.Equal(0, f.Network.ProgressDiagnostics.UnboundSoulMessages);
        Assert.Equal(0, f.Network.ProgressDiagnostics.UnboundSoulPoints);
        Assert.Equal(2, f.Network.ProgressDiagnostics.AcceptedSoulPoints);
        Assert.Single(f.Gains);
        AssertHandleNeverRestarted(f);
    }

    [Theory]
    [InlineData("factory")]
    [InlineData("open")]
    [InlineData("start")]
    public void AdditionalHandleFailuresPreserveThePrimaryAndRetryWithAFreshObject(string failure)
    {
        using var f = new Fixture();
        f.Bind();
        var coverage = f.Coverage;
        var stream = f.Streams[World];
        var generation = f.Generation;
        if (failure == "factory") f.FactoryError = new InvalidOperationException("Synthetic factory failure");
        else f.ConfigureNextHandle = handle =>
        {
            if (failure == "open") handle.OpenError = new InvalidOperationException("Synthetic open failure");
            else handle.StartError = new InvalidOperationException("Synthetic start failure");
        };
        f.Network.UpdateCaptureTarget(Target(World, Web, Added));
        Assert.True(f.Network.StatusIsError);
        Assert.Same(coverage, f.Coverage);
        Assert.True(f.Target.Connections!.SetEquals(new[] { World, Web }));
        Assert.Equal(generation, f.Generation);
        Assert.Same(stream.Tcp, f.Streams[World].Tcp);
        Assert.Same(stream.Messages, f.Streams[World].Messages);
        Assert.True(f.Network.ProgressSources.InitialProgressConfirmed);
        AssertHandleNeverRestarted(f);
        Assert.All(f.Extensions, handle =>
        {
            Assert.Equal(1, handle.Calls.Count("Close"));
            Assert.True(handle.Calls.NativeCallsOutsideGate);
        });

        f.FactoryError = null;
        f.Network.UpdateCaptureTarget(Target(World, Web, Added));
        Assert.False(f.Network.StatusIsError);
        Assert.Equal(2, f.FactoryCalls);
        var live = f.Extensions.Last();
        Assert.Equal(1, live.Calls.Count("Open"));
        Assert.Equal(1, live.Calls.Count("StartCapture"));
        Assert.Equal(0, live.Calls.Count("Close"));
        Assert.Single(live.Calls.Filters);
        AssertHandleNeverRestarted(f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshNativeProofAcceptsEarlyTuplesFromEitherLiveSenderAndDeduplicatesByTcpSequence(bool additionalSender)
    {
        using var f = new Fixture();
        f.Bind();
        f.Network.UpdateCaptureTarget(Target(World, Web, Added));
        var sender = additionalSender ? f.Extensions.Single().Device : f.Device;
        var newWorld = (additionalSender ? Added : World) with { LocalPort = 50009 };
        f.Network.IncomingOwnershipReader = () => Target(Web, newWorld);
        var login = Login();
        var sequence = f.Next(newWorld);
        var packets = f.Network.Packets;
        f.Send(newWorld, login, sender);
        await f.WaitForOwnership();
        f.Network.ProcessDevicePacket(sender, Capture(newWorld, sequence, login));
        await f.WaitForOwnership();

        Assert.Equal(Own.Name, f.Network.Character);
        Assert.NotNull(f.Network.PetBaselineAt);
        Assert.Equal(10, f.Progress.Souls("fossa"));
        Assert.Equal(5, f.Progress.AvailableSouls("fossa"));
        Assert.Equal(2, Assert.Single(f.Gains).Souls);
        Assert.Equal(packets + 2, f.Network.Packets);
        Assert.True(f.Target.Connections!.Contains(newWorld));
        Assert.Equal(0, f.Network.IncomingOwnershipQueue.Packets);
        Assert.Equal(0, f.Network.IncomingOwnershipQueue.Bytes);
        AssertHandleNeverRestarted(f);
        Assert.True(f.Extensions.Single().Calls.NativeCallsOutsideGate);
    }

    [Fact]
    public void TheSameOwnedTcpSegmentFromBothHandlesStillCountsItsSoulExactlyOnce()
    {
        using var f = new Fixture();
        f.Bind();
        f.Network.UpdateCaptureTarget(Target(World, Web, Added));
        var sequence = f.Next(World);
        var gain = Frame(Gain());
        var stream = f.Streams[World];
        var packets = f.Network.Packets;
        f.Send(World, gain);
        f.Network.ProcessDevicePacket(f.Extensions.Single().Device, Capture(World, sequence, gain));

        Assert.Equal(10, f.Progress.Souls("fossa"));
        Assert.Equal(2, Assert.Single(f.Gains).Souls);
        Assert.Equal(packets + 2, f.Network.Packets);
        Assert.Same(stream.Tcp, f.Streams[World].Tcp);
        Assert.Same(stream.Messages, f.Streams[World].Messages);
        Assert.Empty(f.Gaps);
        AssertHandleNeverRestarted(f);
    }

    [Fact]
    public async Task AdditionalSenderCannotAuthorizeAnUnprovedTupleDespiteInstalledEndpointCoverage()
    {
        using var f = new Fixture();
        f.Bind();
        f.Network.UpdateCaptureTarget(Target(World, Web, Added));
        var sender = f.Extensions.Single().Device;
        var foreign = Added with { LocalPort = 50009 };
        var baseline = f.Network.PetBaselineAt;
        f.Send(foreign, Login(), sender);
        await f.WaitForOwnership();

        Assert.Equal(Own.Name, f.Network.Character);
        Assert.Equal(baseline, f.Network.PetBaselineAt);
        Assert.Equal(8, f.Progress.Souls("fossa"));
        Assert.Empty(f.Gains);
        Assert.False(f.Streams.ContainsKey(foreign));
        Assert.Equal(0, f.Network.IncomingOwnershipQueue.Packets);
        AssertHandleNeverRestarted(f);
    }

    [Fact]
    public void AdditionalSenderIsPublishedBeforeStartCaptureCanDeliverTheFirstLogin()
    {
        using var f = new Fixture();
        f.Bind();
        f.ConfigureNextHandle = handle => handle.BeforeStart = () =>
            f.Send(Added, Login(), f.Extensions.Single().Device);
        f.Network.UpdateCaptureTarget(Target(Web, Added));

        Assert.Equal(Own.Name, f.Network.Character);
        Assert.NotNull(f.Network.PetBaselineAt);
        Assert.Equal(10, f.Progress.Souls("fossa"));
        Assert.Equal(2, Assert.Single(f.Gains).Souls);
        Assert.True(f.Extensions.Single().Calls.NativeCallsOutsideGate);
        AssertHandleNeverRestarted(f);
    }

    [Fact]
    public async Task StartFailureRollsBackAdditionalQuarantineAndRejectsItsLateCallbacks()
    {
        using var f = new Fixture();
        f.Bind();
        var coverage = f.Coverage;
        var stream = f.Streams[World];
        var unproved = Added with { LocalPort = 50009 };
        f.ConfigureNextHandle = handle =>
        {
            handle.BeforeStart = () => f.Send(unproved, Login(), f.Extensions.Single().Device);
            handle.StartError = new InvalidOperationException("Synthetic start failure after callback");
        };
        f.Network.UpdateCaptureTarget(Target(World, Web, Added));
        await f.WaitForOwnership();
        var failed = f.Extensions.Single();
        var packets = f.Network.Packets;
        f.Network.ProcessDevicePacket(failed.Device, Capture(World, f.Next(World), Frame(Gain())));

        Assert.True(f.Network.StatusIsError);
        Assert.Same(coverage, f.Coverage);
        Assert.True(f.Target.Connections!.SetEquals(new[] { World, Web }));
        Assert.Equal(Own.Name, f.Network.Character);
        Assert.True(f.Network.ProgressSources.InitialProgressConfirmed);
        Assert.Same(stream.Tcp, f.Streams[World].Tcp);
        Assert.Same(stream.Messages, f.Streams[World].Messages);
        Assert.Equal(8, f.Progress.Souls("fossa"));
        Assert.Equal(packets, f.Network.Packets);
        Assert.Equal(0, f.Network.IncomingOwnershipQueue.Packets);
        Assert.Equal(0, f.Network.IncomingOwnershipQueue.Bytes);
        Assert.Equal(1, failed.Calls.Count("StopCapture"));
        Assert.Equal(1, failed.Calls.Count("Close"));
        Assert.True(failed.Calls.NativeCallsOutsideGate);
        AssertHandleNeverRestarted(f);
    }

    [Fact]
    public async Task StopFromTheFirstAdditionalGainDuringStartClosesAllHandlesWithoutReopening()
    {
        using var f = new Fixture();
        f.Bind();
        f.Network.SoulGained += _ => f.Network.Stop();
        f.ConfigureNextHandle = handle => handle.BeforeStart = () =>
            f.Send(Added, Login(), f.Extensions.Single().Device);
        f.Network.UpdateCaptureTarget(Target(Web, Added));
        await f.Network.DeviceLifecycleTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(f.Network.Running);
        Assert.Null(f.Network.Character);
        Assert.Empty(f.Coverage);
        Assert.Equal(1, f.FactoryCalls);
        Assert.Equal(1, f.Handle.Count("StopCapture"));
        Assert.Equal(1, f.Handle.Count("Close"));
        Assert.Empty(f.Handle.Filters);
        Assert.All(f.Extensions, handle =>
        {
            Assert.Equal(1, handle.Calls.Count("StopCapture"));
            Assert.Equal(1, handle.Calls.Count("Close"));
            Assert.True(handle.Calls.NativeCallsOutsideGate);
        });
        Assert.True(f.Handle.NativeCallsOutsideGate);
    }

    [Fact]
    public async Task StopClosesEveryHandleAndOldSendersRemainRejectedAfterANewSessionStarts()
    {
        using var f = new Fixture();
        f.Bind();
        f.Network.UpdateCaptureTarget(Target(World, Web, Added));
        var another = Added with { RemoteAddress = "203.0.113.2", LocalPort = 50003 };
        f.Network.UpdateCaptureTarget(Target(World, Web, Added, another));
        var retired = f.Extensions.ToArray();
        Assert.Equal(2, retired.Length);
        f.Network.Stop();
        await f.Network.DeviceLifecycleTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, f.Handle.Count("StopCapture"));
        Assert.Equal(1, f.Handle.Count("Close"));
        Assert.All(retired, handle =>
        {
            Assert.Equal(1, handle.Calls.Count("StopCapture"));
            Assert.Equal(1, handle.Calls.Count("Close"));
            Assert.True(handle.Calls.NativeCallsOutsideGate);
        });
        Assert.Empty(f.Coverage);

        f.RestartSynthetic();
        var packets = f.Network.Packets;
        foreach (var sender in retired.Select(handle => handle.Device).Append(f.Device))
            f.Network.ProcessDevicePacket(sender, Capture(World, 1000, Login()));
        Assert.True(f.Network.Running);
        Assert.Equal(packets, f.Network.Packets);
        Assert.Null(f.Network.Character);
        Assert.Empty(f.Streams);
        Assert.Empty(f.Handle.Filters);
        Assert.Equal(3, f.FactoryCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncAdditionalCaptureErrorRevokesOnlyItsScopeClearsQuarantineAndRetriesFresh(bool ownsAdditional)
    {
        using var f = new Fixture();
        f.Bind();
        if (ownsAdditional) f.ConfigureNextHandle = handle => handle.BeforeStart = () =>
            f.Send(Added, Login(), f.Extensions.Single().Device);
        f.Network.UpdateCaptureTarget(ownsAdditional ? Target(Web, Added) : Target(World, Web, Added));
        var failed = f.Extensions.Single();
        var savedSouls = f.Progress.Souls("fossa");
        var originalBaseline = f.Network.PetBaselineAt;
        var generation = f.Generation;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var unproved = Added with { LocalPort = 50009 };
        var priorProof = Target(f.Target.Connections!.Append(unproved).ToArray());
        f.Network.IncomingOwnershipReader = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic ownership read was not released.");
            return priorProof;
        };
        f.Send(unproved, Login(), failed.Device);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            failed.Calls.RaiseStopped(failed.Device, CaptureStoppedEventStatus.ErrorWhileCapturing);
            await f.Network.DeviceLifecycleTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(f.Network.Running);
            Assert.Equal(generation, f.Generation);
            Assert.False(f.Coverage.Contains(Added.RemoteEndpoint));
            Assert.DoesNotContain(Added, f.Target.Connections!);
            Assert.Equal(savedSouls, f.Progress.Souls("fossa"));
            Assert.Equal(0, f.Network.IncomingOwnershipQueue.Packets);
            Assert.Equal(0, f.Network.IncomingOwnershipQueue.Bytes);
            Assert.Equal(1, failed.Calls.Count("Close"));
            if (ownsAdditional)
            {
                Assert.Null(f.Network.Character);
                Assert.False(f.Network.ProgressSources.HasAnyBaseline);
            }
            else
            {
                Assert.Equal(Own.Name, f.Network.Character);
                Assert.Equal(originalBaseline, f.Network.PetBaselineAt);
                Assert.True(f.Network.ProgressSources.InitialProgressConfirmed);
            }
            var packets = f.Network.Packets;
            f.Network.ProcessDevicePacket(failed.Device, Capture(World, f.Next(World), Frame(Gain())));
            Assert.Equal(packets, f.Network.Packets);
            AssertHandleNeverRestarted(f);
        }
        finally { release.Set(); }
        await f.WaitForOwnership();
        Assert.DoesNotContain(unproved, f.Target.Connections!);
        f.Network.IncomingOwnershipReader = () => f.Target;
        f.Network.UpdateCaptureTarget(ownsAdditional ? Target(Web, Added) : Target(World, Web, Added));
        Assert.Equal(2, f.Extensions.Count);
        Assert.NotSame(failed.Device, f.Extensions.Last().Device);
        Assert.Contains(Added.RemoteEndpoint, f.Coverage);
        Assert.Contains(Added, f.Target.Connections!);
        Assert.Equal(1, f.Extensions.Last().Calls.Count("StartCapture"));
        Assert.True(failed.Calls.NativeCallsOutsideGate);
        AssertHandleNeverRestarted(f);
    }

    [Fact]
    public void CompletedWithoutErrorDoesNotRevokeAnAdditionalSenderOrItsCoverage()
    {
        using var f = new Fixture();
        f.Bind();
        f.Network.UpdateCaptureTarget(Target(World, Web, Added));
        var additional = f.Extensions.Single();
        var coverage = f.Coverage;
        var target = f.Target;
        var baseline = f.Network.PetBaselineAt;
        additional.Calls.RaiseStopped(additional.Device, CaptureStoppedEventStatus.CompletedWithoutError);

        Assert.Same(coverage, f.Coverage);
        Assert.Same(target, f.Target);
        Assert.Equal(baseline, f.Network.PetBaselineAt);
        Assert.True(f.Network.ProgressSources.InitialProgressConfirmed);
        Assert.Equal(0, additional.Calls.Count("StopCapture"));
        Assert.Equal(0, additional.Calls.Count("Close"));
        AssertHandleNeverRestarted(f);
    }

    [Fact]
    public void CaptureHandleLimitPreservesAllExistingCapturesInsteadOfMutatingOrRestartingThem()
    {
        using var f = new Fixture();
        f.Bind();
        var connections = new List<CaptureConnection> { World, Web };
        for (var index = 0; index < 31; index++)
        {
            connections.Add(Added with { RemoteAddress = $"203.0.113.{index + 3}", LocalPort = 50020 + index });
            f.Network.UpdateCaptureTarget(Target(connections.ToArray()));
        }
        Assert.Equal(31, f.Extensions.Count);
        var coverage = f.Coverage;
        var target = f.Target;
        var stream = f.Streams[World];
        connections.Add(Added with { RemoteAddress = "203.0.113.99", LocalPort = 50150 });
        f.Network.UpdateCaptureTarget(Target(connections.ToArray()));

        Assert.True(f.Network.StatusIsError);
        Assert.Same(coverage, f.Coverage);
        Assert.Same(target, f.Target);
        Assert.Equal(31, f.FactoryCalls);
        Assert.Equal(Own.Name, f.Network.Character);
        Assert.True(f.Network.ProgressSources.InitialProgressConfirmed);
        Assert.Same(stream.Tcp, f.Streams[World].Tcp);
        Assert.Same(stream.Messages, f.Streams[World].Messages);
        Assert.All(f.Extensions, handle =>
        {
            Assert.Single(handle.Calls.Filters);
            Assert.Equal(1, handle.Calls.Count("StartCapture"));
            Assert.Equal(0, handle.Calls.Count("StopCapture"));
            Assert.Equal(0, handle.Calls.Count("Close"));
            Assert.True(handle.Calls.NativeCallsOutsideGate);
        });
        f.Send(World, Frame(Gain()));
        Assert.Equal(10, f.Progress.Souls("fossa"));
        Assert.Single(f.Gains);
        AssertHandleNeverRestarted(f);
    }

    [Fact]
    public async Task StartRollbackCannotReviveAnotherHandleThatDiedDuringTheSameStart()
    {
        using var f = new Fixture();
        f.Bind();
        f.Network.UpdateCaptureTarget(Target(World, Web, Added));
        var first = f.Extensions.Single();
        var another = Added with { RemoteAddress = "203.0.113.2", LocalPort = 50003 };
        f.ConfigureNextHandle = handle =>
        {
            handle.BeforeStart = () => first.Calls.RaiseStopped(first.Device, CaptureStoppedEventStatus.ErrorWhileCapturing);
            handle.StartError = new InvalidOperationException("Synthetic second start failure after first handle died");
        };
        f.Network.UpdateCaptureTarget(Target(World, Web, Added, another));
        await f.Network.DeviceLifecycleTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(f.Coverage.SetEquals(new[] { World.RemoteEndpoint, Web.RemoteEndpoint }));
        Assert.True(f.Target.Connections!.SetEquals(new[] { World, Web }));
        Assert.Equal(Own.Name, f.Network.Character);
        Assert.True(f.Network.ProgressSources.InitialProgressConfirmed);
        Assert.Equal(2, f.Extensions.Count);
        Assert.All(f.Extensions, handle => Assert.Equal(1, handle.Calls.Count("Close")));
        AssertHandleNeverRestarted(f);

        f.Network.UpdateCaptureTarget(Target(World, Web, Added, another));
        Assert.Equal(3, f.Extensions.Count);
        var replacement = f.Extensions.Last();
        Assert.NotSame(first.Device, replacement.Device);
        Assert.NotSame(f.Extensions[1].Device, replacement.Device);
        Assert.True(f.Coverage.Contains(Added.RemoteEndpoint));
        Assert.True(f.Coverage.Contains(another.RemoteEndpoint));
        Assert.Equal("tcp and ((host 203.0.113.1 and port 443) or (host 203.0.113.2 and port 443))",
            Assert.Single(replacement.Calls.Filters));
        f.Send(World, Frame(Gain()));
        Assert.Equal(10, f.Progress.Souls("fossa"));
        Assert.Single(f.Gains);
        AssertHandleNeverRestarted(f);
    }

    [Fact]
    public async Task StopFromAQuarantineInvalidationDuringStartRollbackCannotReviveCoverageOrOverwriteStoppedStatus()
    {
        using var f = new Fixture();
        f.Bind();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var pending = Added with { LocalPort = 50009 };
        f.Network.IncomingOwnershipReader = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic ownership read was not released.");
            return Target(World, Web, Added, pending);
        };
        var stoppedDuringRollback = false;
        f.Network.Changed += () =>
        {
            if (f.Network.ProgressDiagnostics.LastResetReason != "incoming-dropped") return;
            stoppedDuringRollback = true;
            f.Network.Stop();
        };
        f.ConfigureNextHandle = handle =>
        {
            handle.BeforeStart = () =>
            {
                f.Send(pending, Login(), f.Extensions.Single().Device);
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                // A native target refresh can prove a tuple while its earlier first packet remains
                // behind the quarantine barrier. A bound Own makes its later loss authoritative.
                f.Network.UpdateCaptureTarget(Target(World, Web, Added, pending));
                f.Network.ObserveIdentity(pending.StreamKey, Own, At);
            };
            handle.StartError = new InvalidOperationException("Synthetic start failure with owned quarantine");
        };
        try
        {
            f.Network.UpdateCaptureTarget(Target(World, Web, Added));
            await f.Network.DeviceLifecycleTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.Set(); }
        await f.WaitForOwnership();

        Assert.True(stoppedDuringRollback);
        Assert.False(f.Network.Running);
        Assert.Null(f.Network.Character);
        Assert.Null(f.Target);
        Assert.Empty(f.Coverage);
        Assert.Equal("Netzwerk-Quelle gestoppt.", f.Network.Status);
        Assert.Equal(0, f.Network.IncomingOwnershipQueue.Packets);
        Assert.Equal(0, f.Network.IncomingOwnershipQueue.Bytes);
        Assert.Equal(1, f.Extensions.Single().Calls.Count("Close"));
        Assert.Equal(1, f.Extensions.Single().Calls.Count("StopCapture"));
        Assert.Equal(1, f.Handle.Count("Close"));
        Assert.Equal(1, f.Handle.Count("StopCapture"));
        Assert.True(f.Handle.NativeCallsOutsideGate);
        Assert.True(f.Extensions.Single().Calls.NativeCallsOutsideGate);
        Assert.Empty(f.Handle.Filters);
    }

    // SharpPcap 6.3.1 exposes no public PcapException constructors.
    private static PcapException PcapFailure() => (PcapException)Activator.CreateInstance(typeof(PcapException), nonPublic: true)!;

    private static void AssertHandleNeverRestarted(Fixture f)
    {
        Assert.Equal(0, f.Handle.Count("Open"));
        Assert.Equal(0, f.Handle.Count("StartCapture"));
        Assert.Equal(0, f.Handle.Count("StopCapture"));
        Assert.Equal(0, f.Handle.Count("Close"));
        Assert.Empty(f.Handle.Filters);
    }

    public class DeviceProxy : DispatchProxy
    {
        private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);
        public object Gate { get; set; } = new();
        public List<string> Filters { get; } = [];
        public Exception? FilterError { get; set; }
        public Exception? OpenError { get; set; }
        public Exception? StartError { get; set; }
        public Action<string>? BeforeFilter { get; set; }
        public Action? BeforeStart { get; set; }
        private CaptureStoppedEventHandler? _stopped;
        public void RaiseStopped(ILiveDevice sender, CaptureStoppedEventStatus status) => _stopped?.Invoke(sender, status);
        public bool NativeCallsOutsideGate { get; private set; } = true;
        public int Count(string method) { lock (_calls) return _calls.GetValueOrDefault(method); }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod!;
            lock (_calls) _calls[method.Name] = Count(method.Name) + 1;
            if (method.Name is "Open" or "set_Filter" or "StartCapture" or "StopCapture" or "Close")
                NativeCallsOutsideGate &= !Monitor.IsEntered(Gate);
            if (method.Name == "add_OnCaptureStopped") _stopped += (CaptureStoppedEventHandler)args![0]!;
            if (method.Name == "remove_OnCaptureStopped") _stopped -= (CaptureStoppedEventHandler)args![0]!;
            if (method.Name == "Open" && OpenError is { } open) throw open;
            if (method.Name == "set_Filter")
            {
                var filter = (string)args![0]!;
                Filters.Add(filter); BeforeFilter?.Invoke(filter);
                if (FilterError is { } error) throw error;
            }
            if (method.Name == "StartCapture")
            {
                BeforeStart?.Invoke();
                if (StartError is { } error) throw error;
            }
            return method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }

    private sealed record SyntheticHandle(ILiveDevice Device, DeviceProxy Calls);

    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-filter-expand-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<CaptureConnection, uint> _sequences = [];
        private long _ticks = At.Ticks;
        public ProgressService Progress { get; }
        public NetworkLootService Network { get; }
        public ILiveDevice Device { get; }
        public DeviceProxy Handle { get; }
        public List<SyntheticHandle> Extensions { get; } = [];
        public Action<DeviceProxy>? ConfigureNextHandle { get; set; }
        public Exception? FactoryError { get; set; }
        public int FactoryCalls { get; private set; }
        public List<NetworkSoul> Gains { get; } = [];
        public List<string> Gaps { get; } = [];
        public object Gate => Field("_gate").GetValue(Network)!;
        public long Generation => (long)Field("_generation").GetValue(Network)!;
        public CaptureTarget Target => (CaptureTarget)Field("_target").GetValue(Network)!;
        public IReadOnlySet<CaptureEndpoint> Coverage => (IReadOnlySet<CaptureEndpoint>)Field("_installedCoverage").GetValue(Network)!;
        public Dictionary<CaptureConnection, (TcpReassembler Tcp, GameMessageStream Messages)> Streams =>
            (Dictionary<CaptureConnection, (TcpReassembler Tcp, GameMessageStream Messages)>)Field("_streams").GetValue(Network)!;
        public Fixture()
        {
            Directory.CreateDirectory(_folder);
            Progress = new(Path.Combine(_folder, "progress.json"), new PetCatalog([
                new PetDefinition { Id = "fossa", En = "Fossa", Genus = "Fera", NumericId = "1004" }]));
            var exploration = new ExplorationService(Path.Combine(_folder, "exploration.json"), []);
            Network = new(Progress, exploration);
            var target = NetworkCaptureFilterExpansionTests.Target(World, Web);
            Device = DispatchProxy.Create<ILiveDevice, DeviceProxy>();
            Handle = (DeviceProxy)(object)Device; Handle.Gate = Gate;
            Field("_device").SetValue(Network, Device); Field("_target").SetValue(Network, target);
            Field("_installedCoverage").SetValue(Network, target.RemoteEndpoints);
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
            Network.CaptureDeviceFactory = _ =>
            {
                FactoryCalls++;
                if (FactoryError is { } error) throw error;
                var device = DispatchProxy.Create<ILiveDevice, DeviceProxy>();
                var calls = (DeviceProxy)(object)device; calls.Gate = Gate;
                Extensions.Add(new(device, calls));
                var configure = ConfigureNextHandle; ConfigureNextHandle = null;
                configure?.Invoke(calls);
                return device;
            };
            Network.IncomingOwnershipUtcNow = () => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
            Network.IncomingOwnershipWait = delay => { Interlocked.Add(ref _ticks, delay.Ticks); return Task.CompletedTask; };
            Network.IncomingOwnershipReader = () => Target;
            Network.PeriodicOwnershipReader = () => Target;
            Network.SoulGained += Gains.Add; Network.BossStreamGap += Gaps.Add;
        }
        public void Bind()
        {
            Send(World, Frame(Clock()));
            Network.ObserveIdentity(World.StreamKey, Own, At);
            Send(World, Frame(Pets()));
            Send(World, Frame([0x09, 0x8D, 0]));
            Send(World, Frame([0x32, 0x92, 0]));
            Gaps.Clear();
            Assert.True(Network.ProgressSources.InitialProgressConfirmed);
        }
        public uint Next(CaptureConnection connection) => _sequences.GetValueOrDefault(connection, 1000u);
        public void Send(CaptureConnection connection, byte[] payload, ILiveDevice? device = null)
        {
            var sequence = Next(connection);
            _sequences[connection] = sequence + (uint)payload.Length;
            Network.ProcessDevicePacket(device ?? Device, Capture(connection, sequence, payload));
        }
        public Task WaitForOwnership() => Network.IncomingOwnershipTask.WaitAsync(TimeSpan.FromSeconds(5));
        public void RestartSynthetic()
        {
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
            Network.UpdateCaptureTarget(NetworkCaptureFilterExpansionTests.Target(World, Web));
        }
        private static FieldInfo Field(string name) => typeof(NetworkLootService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        public void Dispose() { Network.Dispose(); Directory.Delete(_folder, recursive: true); }
    }

    private static CaptureTarget Target(params CaptureConnection[] connections) => CaptureTarget.FromRows(
        connections.Select(c => c.GamePid).ToHashSet(), connections.Select(c => new TcpTable.Row(
            new IPEndPoint(IPAddress.Parse(c.LocalAddress), c.LocalPort), new IPEndPoint(IPAddress.Parse(c.RemoteAddress), c.RemotePort), c.GamePid, true)).ToArray(),
        _ => "adapter", _ => "Synthetic relay")!;
    private static byte[] Clock() => [0, 0x36, .. BitConverter.GetBytes(1791849600000L)];
    private static byte[] Gain() => [0x0D, 0x90, 1, .. BitConverter.GetBytes(1004), .. BitConverter.GetBytes(2)];
    private static byte[] Pets() => [0, 0x90, .. BitConverter.GetBytes(0), 1, .. BitConverter.GetBytes(999), .. BitConverter.GetBytes(1004),
        .. BitConverter.GetBytes(1), 1, .. BitConverter.GetBytes(1004), .. BitConverter.GetBytes(3), 0, 0, 0, 0];
    private static byte[] Frame(byte[] message)
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        V(writer, (uint)(message.Length + 4)); writer.Write(message); return bytes.ToArray();
    }
    private static void V(BinaryWriter writer, uint value)
    {
        while (value >= 128) { writer.Write((byte)(value | 128)); value >>= 7; }
        writer.Write((byte)value);
    }
    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        writer.Write(opcode); write(writer); return bytes.ToArray();
    }
    private static byte[] Candidate() => Packet(0x3620, writer =>
    {
        writer.Write((ushort)0); V(writer, (uint)Own.EntityKey); writer.Write(new byte[4]); writer.Write(Own.CharacterId);
        writer.Write(new byte[2279 - (int)(writer.BaseStream.Position - 2)]);
    });
    private static byte[] OwnMessage() => Packet(0x3633, writer =>
    {
        V(writer, (uint)Own.EntityKey); writer.Write(0);
        writer.Write((byte)1); var name = Encoding.UTF8.GetBytes(Own.Name); V(writer, (uint)name.Length); writer.Write(name);
        writer.Write((ushort)Own.ServerId); writer.Write(0); writer.Write(new byte[36]);
        V(writer, 0); V(writer, 0); writer.Write(new byte[16]); writer.Write((byte)0);
        writer.Write((ushort)0); writer.Write(new byte[18]); writer.Write((byte)1); V(writer, 0); V(writer, 0); writer.Write(new byte[52]);
        V(writer, 0); V(writer, 0); V(writer, 0);
        writer.Write(new byte[12]); writer.Write(Own.CharacterId); V(writer, 0); writer.Write((byte)1);
        writer.Write(new byte[32]); V(writer, 0); V(writer, 0);
    });
    private static byte[] Login() => [.. Frame(Clock()), .. Frame(Candidate()), .. Frame(Pets()),
        .. Frame([0x21, 0x36, .. new byte[46]]), .. Frame(OwnMessage()), .. Frame(Gain())];
    private static RawCapture Capture(CaptureConnection connection, uint sequence, byte[] payload)
    {
        var bytes = new byte[54 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), 0x0800); bytes[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(16), checked((ushort)(bytes.Length - 14))); bytes[22] = 64; bytes[23] = 6;
        IPAddress.Parse(connection.RemoteAddress).GetAddressBytes().CopyTo(bytes, 26);
        IPAddress.Parse(connection.LocalAddress).GetAddressBytes().CopyTo(bytes, 30);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(34), checked((ushort)connection.RemotePort));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(36), checked((ushort)connection.LocalPort));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(38), sequence); bytes[46] = 0x50; bytes[47] = 0x10;
        payload.CopyTo(bytes, 54);
        return new(LinkLayers.Ethernet, new PosixTimeval(1791849600UL, 0UL), bytes, null);
    }
}
