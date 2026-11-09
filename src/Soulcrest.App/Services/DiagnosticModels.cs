namespace Soulcrest.App.Services;

public sealed record DiagnosticFeature(string Id, string Group, string Label, IReadOnlyList<string> Dependencies);

public enum DiagnosticStatus { Healthy, Warning, Error, Disabled, Waiting, Unavailable }

public sealed record DiagnosticResult(string FeatureId, DiagnosticStatus Status, DateTimeOffset CheckedAt,
    string Summary, IReadOnlyDictionary<string, object?> Facts);

public interface IDiagnosticContributor
{
    IReadOnlySet<string> FeatureIds { get; }
    DiagnosticResult Collect(string featureId);
}
