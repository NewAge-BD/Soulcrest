namespace Soulcrest.App.Services;

/// <summary>Anonymous, passive projection of already observed recognition work.</summary>
internal static class DaevanionRecognitionDiagnosticFacts
{
    internal static void Append(List<(string, object?)> facts, DaevanionRecognitionHealth? health)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var last = health?.Last;
        facts.Add(("recognitionSearches", health?.Searches));
        facts.Add(("recognitionPreferredSearches", health?.PreferredSearches));
        facts.Add(("recognitionMatches", health?.Matches));
        facts.Add(("recognitionMisses", health?.Misses));
        facts.Add(("recognitionExpiredAfterSearch", health?.ExpiredAfterSearch));
        facts.Add(("recognitionPath", last is null ? null
            : Enum.IsDefined(last.Path) ? last.Path.ToString() : "Unknown"));
        facts.Add(("recognitionElapsedMs", Finite(last?.ElapsedMilliseconds)));
        facts.Add(("recognitionComputedScores", last?.ComputedScores));
        facts.Add(("recognitionReusedScores", last?.ReusedScores));
        facts.Add(("recognitionMaximumMs", Finite(health?.MaximumMilliseconds)));
    }

    private static double? Finite(double? value) => value is { } number && double.IsFinite(number) ? number : null;
}
