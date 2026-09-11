using System.Reflection;

namespace KeelMatrix.CompatRadar;

internal static class RadarContract
{
    public const int SchemaVersion = 1;
    public static string ToolVersion => typeof(RadarContract).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "CompatRadar.ToolVersion")?.Value
        ?? typeof(RadarContract).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
    public const string ConfigurationFileName = "compat-radar.json";
    public const string DefaultReportFileName = "compat-radar-report.json";
    public const int OutputLimitBytes = 64 * 1024;
}

internal enum WatchKind
{
    NuGetPrerelease,
    SdkPreview,
    RuntimePreview
}

internal enum ResultClassification
{
    Compatible,
    FutureRegression,
    InconclusiveBaselineFailed,
    InconclusiveFlaky,
    InconclusiveExecution,
    Unsupported
}

internal sealed record RadarConfiguration(
    int Version,
    ControlConfiguration Control,
    IReadOnlyList<WatchConfiguration> Watches,
    ValidationConfiguration Validation,
    PolicyConfiguration Policy,
    string RepositoryRelativePath);

internal sealed record ControlConfiguration(string Sdk);

internal sealed record WatchConfiguration(
    string Id,
    WatchKind Kind,
    string? Package,
    IReadOnlyList<string> Candidates,
    string? Feed);

internal sealed record ValidationConfiguration(
    string Command,
    string WorkingDirectory,
    int TimeoutSeconds);

internal sealed record PolicyConfiguration(int ConfirmationRuns);

internal sealed record CandidateVersion(IReadOnlyList<int> Core, IReadOnlyList<CandidateIdentifier> Prerelease)
{
    public override string ToString() => string.Join('.', Core) + (Prerelease.Count == 0
        ? string.Empty
        : $"-{string.Join('.', Prerelease.Select(x => x.Value))}");
}

internal sealed record CandidateIdentifier(string Value, bool IsNumeric, int NumericValue);

internal sealed record CandidateValidationResult(
    bool Accepted,
    IReadOnlyList<string> OrderedCandidates,
    IReadOnlyList<string> RejectedCandidates,
    string? Error);

internal sealed record ProcessAttempt(
    int ExitCode,
    bool TimedOut,
    bool Cancelled,
    bool TerminationRequested,
    string? FailureKind,
    string Summary,
    string NormalizedSignature,
    string Fingerprint);

internal sealed record RunEvidence(
    string Classification,
    bool CandidateEvaluated,
    string Command,
    string WorkingDirectory,
    string? RestoreCommand,
    string Summary,
    string NormalizedSignature,
    string Fingerprint,
    IReadOnlyList<ProcessAttempt> Attempts);

internal sealed record ReproductionWitness(
    int SchemaVersion,
    string RepositoryRevision,
    string Configuration,
    string WorkingDirectory,
    string ValidationCommand,
    string? RestoreCommand,
    string? Package,
    string? Candidate,
    string? Feed,
    string? Sdk,
    string? FocusedFailure,
    string NormalizedFailureSignature,
    string Fingerprint,
    string ReproductionHint)
{
    public string ControlConfiguration { get; init; } = string.Empty;
    public string CandidateInputConfiguration { get; init; } = string.Empty;
    public IReadOnlyList<ProcessAttempt> StableAttempts { get; init; } = [];
    public IReadOnlyList<ProcessAttempt> CandidateAttempts { get; init; } = [];
    public string ReproductionConfiguration { get; init; } = string.Empty;
    public string RepositoryIdentity { get; init; } = string.Empty;
};

internal sealed record CandidateComparison(
    string FindingId,
    string Candidate,
    ResultClassification Classification,
    bool CandidateEvaluated,
    RunEvidence StableControl,
    RunEvidence CandidateResult,
    ReproductionWitness Witness);

internal sealed record WatchReport(
    string Id,
    string Kind,
    string? Package,
    IReadOnlyList<string> InputCandidates,
    IReadOnlyList<string> OrderedCandidates,
    IReadOnlyList<string> RejectedCandidates,
    bool OrderingValidated,
    string OrderingRule,
    IReadOnlyList<CandidateComparison> Comparisons,
    string? FirstConfirmedBadCandidate,
    IReadOnlyList<string> ObservedFailingCandidates);

internal sealed record RadarReport(
    int SchemaVersion,
    string ToolVersion,
    string RepositoryRevision,
    string ConfigurationPath,
    IReadOnlyList<WatchReport> Watches,
    IReadOnlyList<CandidateComparison> Findings,
    int ExitCode);

internal sealed record AnalysisResult(
    RadarReport Report,
    bool TrustworthyComparisonCompleted);
