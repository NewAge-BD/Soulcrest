using System.Collections.Immutable;

namespace Soulcrest.App.Services;

public enum ReplayPositionProvenance : byte { Matched, Flow }
public enum ReplayPositionGapReason : byte { SourceInvalidated, FlowBroken, QueueOverflow, WriteFailure, IncompleteTail }

/// <summary>An anonymous visual observation. Frame identity is local to the capture, never a game entity key.</summary>
public sealed record ReplayPositionSample(TimeSpan Elapsed, DateTimeOffset OriginalAt, string MapId,
    string MapName, double X, double Y, ReplayPositionProvenance Provenance, long SegmentId, long FrameId);
public sealed record ReplayPositionGap(TimeSpan Elapsed, ReplayPositionGapReason Reason);
public sealed record ReplayPositionReadResult(ImmutableArray<ReplayPositionSample> Samples,
    ImmutableArray<ReplayPositionGap> Gaps, string SourceFingerprint, bool Exists, bool Truncated);

/// <summary>Qualified shared-capture observation before it is bound to an original loot session.</summary>
public sealed record ReplayMapPositionSample(string MapId, double X, double Y, DateTimeOffset OriginalAt,
    long CapturedAt, long FrameId, long CaptureSessionId, long MapGeneration, long? WorldGeneration,
    long? BindingGeneration, ReplayPositionProvenance Provenance, long SegmentId);
