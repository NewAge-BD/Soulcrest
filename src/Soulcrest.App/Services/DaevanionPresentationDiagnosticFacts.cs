using Soulcrest.App.Overlay;

namespace Soulcrest.App.Services;

internal static class DaevanionPresentationDiagnosticFacts
{
    internal static void Append(List<(string, object?)> facts, DaevanionAnimationHealth? health)
    {
        facts.Add(("highlightPresentedFrames", health?.PresentedFrames));
        facts.Add(("highlightSuppressions", health?.Suppressions));
        facts.Add(("highlightNativeBufferAllocations", health?.NativeBufferAllocations));
        facts.Add(("highlightRasterMs", Finite(health?.LastRasterMilliseconds)));
        facts.Add(("highlightMaximumRasterMs", Finite(health?.MaximumRasterMilliseconds)));
        facts.Add(("highlightTransferMs", Finite(health?.LastTransferMilliseconds)));
        facts.Add(("highlightMaximumTransferMs", Finite(health?.MaximumTransferMilliseconds)));
        facts.Add(("highlightFrameIntervalMs", Finite(health?.LastFrameIntervalMilliseconds)));
        facts.Add(("highlightMaximumFrameIntervalMs", Finite(health?.MaximumFrameIntervalMilliseconds)));
        facts.Add(("highlightTotalMs", Finite(health?.LastTotalMilliseconds)));
        facts.Add(("highlightNextDelayMs", health?.NextFrameDelayMilliseconds));
        facts.Add(("highlightAnimationTicks", health?.AnimationTicks));
        facts.Add(("highlightTickMs", Finite(health?.LastTickMilliseconds)));
        facts.Add(("highlightMaximumTickMs", Finite(health?.MaximumTickMilliseconds)));
        facts.Add(("highlightInitialSourceValidationMs", Finite(health?.LastInitialSourceValidationMilliseconds)));
        facts.Add(("highlightMaximumInitialSourceValidationMs", Finite(health?.MaximumInitialSourceValidationMilliseconds)));
    }

    private static double? Finite(double? value) => value is { } number && double.IsFinite(number) && number >= 0 ? number : null;
}
