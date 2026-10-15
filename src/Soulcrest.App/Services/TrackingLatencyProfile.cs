using System.Diagnostics;
using Soulcrest.App.Capture;

namespace Soulcrest.App.Services;

/// <summary>TrackingCost serializes updates, then exclusively drains its inactive buffer outside the producer gate.</summary>
internal sealed class TrackingLatencyProfile
{
    private readonly LatencyWindow _captureGate = new(), _captureCopy = new(), _captureMap = new(), _captureConversion = new(), _captureTotal = new();
    private readonly LatencyWindow _sourceAge = new();
    private readonly LatencyWindow _flowAge = new(), _composeAll = new(), _composeShown = new(), _ageAtCompose = new();
    private readonly LatencyWindow _uiWait = new(), _uiWork = new(), _present = new(), _overlayAge = new(), _presentationGap = new();
    private long _lastPresentation;
    private long _flowSteps, _flowNulls, _worldFlowSteps, _worldFlowNulls, _worldChecks, _worldMisses, _captureNulls, _captureRejected;
    private long _sourceSession, _sourceFrame, _lastArrived, _lastPublished, _lastFrameGateDrops;
    private long _sourceSamples, _sourceNew, _sourceRepeats, _sourceRegressions, _arrivedDelta, _publishedDelta, _frameGateDropDelta;

    internal void AddCapture(CaptureTiming timing)
    {
        if (!timing.Valid) return;
        _captureGate.Add(timing.GateWaitMs);
        _captureCopy.Add(timing.CopyMs);
        _captureMap.Add(timing.MapWaitMs);
        _captureConversion.Add(timing.ConversionMs);
        _captureTotal.Add(timing.TotalMs);
        if (timing.CaptureSessionId <= 0 || timing.SourceFrameId <= 0) return;
        _sourceAge.Add(timing.SourceAgeAtCopyMs);
        _sourceSamples++;
        if (_sourceSession != timing.CaptureSessionId)
        {
            _sourceNew++;
            _sourceSession = timing.CaptureSessionId;
        }
        else
        {
            if (timing.SourceFrameId == _sourceFrame) _sourceRepeats++;
            else if (timing.SourceFrameId > _sourceFrame) _sourceNew++;
            else _sourceRegressions++;
            // A newly observed session establishes a baseline; its earlier history is not this window.
            _arrivedDelta += Math.Max(0, timing.ArrivedCount - _lastArrived);
            _publishedDelta += Math.Max(0, timing.PublishedCount - _lastPublished);
            _frameGateDropDelta += Math.Max(0, timing.FrameGateDropCount - _lastFrameGateDrops);
        }
        _sourceFrame = timing.SourceFrameId;
        _lastArrived = timing.ArrivedCount;
        _lastPublished = timing.PublishedCount;
        _lastFrameGateDrops = timing.FrameGateDropCount;
    }

    internal void AddFlow(bool unavailable, bool worldMap)
    {
        _flowSteps++;
        if (unavailable) _flowNulls++;
        if (!worldMap) return;
        _worldFlowSteps++;
        if (unavailable) _worldFlowNulls++;
    }

    internal void AddFlowAge(double milliseconds) => _flowAge.Add(milliseconds);
    internal void AddComposition(double milliseconds) => _composeAll.Add(milliseconds);
    internal void AddCaptureUnavailable() => _captureNulls++;
    internal void AddCaptureRejected() => _captureRejected++;
    internal void AddWorldDetection(bool found) { _worldChecks++; if (!found) _worldMisses++; }

    /// <summary>Only frames actually handed to Windows contribute to shown-stage distributions.</summary>
    internal void AddPresentation(OverlayTiming timing)
    {
        AddElapsed(_overlayAge, timing.CapturedAt, timing.PresentedAt);
        AddElapsed(_ageAtCompose, timing.CapturedAt, timing.ComposeStartedAt);
        AddElapsed(_composeShown, timing.ComposeStartedAt, timing.ComposedAt);
        AddElapsed(_uiWait, timing.ComposedAt, timing.UiEnteredAt);
        AddElapsed(_uiWork, timing.UiEnteredAt, timing.PresentStartedAt);
        AddElapsed(_present, timing.PresentStartedAt, timing.PresentedAt);
        AddElapsed(_presentationGap, _lastPresentation, timing.PresentedAt);
        if (timing.PresentedAt > _lastPresentation) _lastPresentation = timing.PresentedAt;
    }

    // A deliberate hide/suppression/map context change is not an active overlay freeze.
    internal void ResetPresentation() => _lastPresentation = 0;

    // Called while TrackingCost swaps its two fixed buffers, before either is accessed again.
    internal void ContinueFrom(TrackingLatencyProfile previous)
    {
        _lastPresentation = previous._lastPresentation;
        _sourceSession = previous._sourceSession;
        _sourceFrame = previous._sourceFrame;
        _lastArrived = previous._lastArrived;
        _lastPublished = previous._lastPublished;
        _lastFrameGateDrops = previous._lastFrameGateDrops;
    }

    private static void AddElapsed(LatencyWindow window, long started, long ended)
    {
        if (started > 0 && ended >= started)
            window.Add(Stopwatch.GetElapsedTime(started, ended).TotalMilliseconds);
    }

    internal TrackingLatencySnapshot Drain()
    {
        var result = new TrackingLatencySnapshot(_captureGate.Drain(), _captureCopy.Drain(), _captureMap.Drain(),
            _captureConversion.Drain(), _captureTotal.Drain(), _sourceAge.Drain(), _flowAge.Drain(), _composeAll.Drain(), _composeShown.Drain(),
            _ageAtCompose.Drain(), _uiWait.Drain(), _uiWork.Drain(), _present.Drain(), _overlayAge.Drain(), _presentationGap.Drain(),
            _flowSteps, _flowNulls, _worldFlowSteps, _worldFlowNulls, _worldChecks, _worldMisses, _captureNulls, _captureRejected,
            _sourceSamples, _sourceNew, _sourceRepeats, _sourceRegressions, _arrivedDelta, _publishedDelta, _frameGateDropDelta);
        _flowSteps = _flowNulls = _worldFlowSteps = _worldFlowNulls = _worldChecks = _worldMisses = _captureNulls = _captureRejected = 0;
        _sourceSamples = _sourceNew = _sourceRepeats = _sourceRegressions = _arrivedDelta = _publishedDelta = _frameGateDropDelta = 0;
        return result;
    }
}

internal readonly record struct OverlayTiming(long CapturedAt, long ComposeStartedAt, long ComposedAt,
    long UiEnteredAt, long PresentStartedAt, long PresentedAt);

internal sealed record TrackingLatencySnapshot(LatencySummary CaptureGate, LatencySummary CaptureCopy,
    LatencySummary CaptureMap, LatencySummary CaptureConversion, LatencySummary CaptureTotal, LatencySummary SourceAge,
    LatencySummary FlowAge, LatencySummary ComposeAll, LatencySummary ComposeShown, LatencySummary AgeAtCompose,
    LatencySummary UiWait, LatencySummary UiWork, LatencySummary Present, LatencySummary OverlayAge, LatencySummary PresentationGap,
    long FlowSteps, long FlowNulls, long WorldFlowSteps, long WorldFlowNulls, long WorldChecks, long WorldMisses,
    long CaptureNulls, long CaptureRejected, long SourceSamples, long SourceNew, long SourceRepeats,
    long SourceRegressions, long ArrivedDelta, long PublishedDelta, long FrameGateDropDelta)
{
    // All values in ms. Mean/max cover the window; p50/p95 cover the bounded retained samples.
    internal string ToLogText() => "profil_ms[mean/p50/p95/max;n/ret] " + string.Join(" ",
        Stage("gate", CaptureGate), Stage("copy", CaptureCopy), Stage("gpu_wait", CaptureMap),
        Stage("convert", CaptureConversion), Stage("capture", CaptureTotal), Stage("source_age_copy", SourceAge), Stage("flow_age", FlowAge),
        Stage("compose_all", ComposeAll), Stage("compose_shown", ComposeShown), Stage("age_at_compose", AgeAtCompose),
        Stage("ui_wait_shown", UiWait), Stage("ui_work_shown", UiWork), Stage("present", Present),
        Stage("age_shown", OverlayAge), Stage("present_gap", PresentationGap)) +
        FormattableString.Invariant($" flow_null={FlowNulls}/{FlowSteps} worldflow_null={WorldFlowNulls}/{WorldFlowSteps} worldcheck_miss={WorldMisses}/{WorldChecks} capture_null={CaptureNulls} capture_reject={CaptureRejected} source_samples={SourceSamples} source_new={SourceNew} source_repeat={SourceRepeats} source_backwards={SourceRegressions} wgc_arrived={ArrivedDelta} wgc_published={PublishedDelta} wgc_gate_drop={FrameGateDropDelta}");

    private static string Stage(string name, LatencySummary s) => FormattableString.Invariant(
        $"{name}={s.MeanMs:0.0}/{s.MedianMs:0.0}/{s.P95Ms:0.0}/{s.MaximumMs:0.0};{s.Count}/{s.Retained}");
}
