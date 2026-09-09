using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeelMatrix.CompatRadar;

internal sealed record EngineValidationResult(bool IsValid, ParsedCommand? Command, IReadOnlyList<string> Errors, string WorkingDirectory);

internal sealed class RadarEngine
{
    public static EngineValidationResult ValidateExecution(string repositoryRoot, RadarConfiguration configuration)
    {
        var errors = new List<string>();
        if (!CommandParser.TryParse(configuration.Validation.Command, out var command, out var commandError))
        {
            errors.Add(commandError!);
        }

        var workingDirectory = Path.GetFullPath(Path.Combine(repositoryRoot, configuration.Validation.WorkingDirectory));
        if (!PathUtilities.IsWithinRoot(repositoryRoot, workingDirectory))
        {
            errors.Add("validation.workingDirectory must stay inside the repository.");
        }
        else if (!Directory.Exists(workingDirectory))
        {
            errors.Add("validation.workingDirectory does not exist.");
        }

        return new EngineValidationResult(errors.Count == 0, command, errors, workingDirectory);
    }

    public async Task<AnalysisResult> AnalyzeAsync(
        string repositoryRoot,
        RadarConfiguration configuration,
        string configurationPath,
        CancellationToken cancellationToken)
    {
        var revision = MaterializationScope.ComputeRepositoryRevision(repositoryRoot);
        var executionValidation = ValidateExecution(repositoryRoot, configuration);
        if (!executionValidation.IsValid)
        {
            var report = new RadarReport(
                RadarContract.SchemaVersion,
                RadarContract.ToolVersion,
                revision,
                configurationPath,
                [],
                [],
                2);
            return new AnalysisResult(report, false);
        }

        using var scope = MaterializationScope.Create();
        var watches = new List<WatchReport>();
        var findings = new List<CandidateComparison>();
        var trustworthyComparison = false;
        var hasUntrustworthyResult = false;

        foreach (var watch in configuration.Watches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ordering = Versioning.Validate(watch.Candidates);
            if (!ordering.Accepted)
            {
                watches.Add(new WatchReport(
                    watch.Id,
                    FormatKind(watch.Kind),
                    watch.Package,
                    watch.Candidates,
                    ordering.OrderedCandidates,
                    ordering.RejectedCandidates,
                    false,
                    ordering.Error ?? "Candidate ordering was rejected before execution.",
                    [],
                    null,
                    []));
                hasUntrustworthyResult = true;
                continue;
            }

            var comparisons = new List<CandidateComparison>();
            foreach (var candidate in ordering.OrderedCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var comparison = await CompareCandidateAsync(
                    repositoryRoot,
                    configuration,
                    configurationPath,
                    revision,
                    executionValidation,
                    scope,
                    watch,
                    candidate,
                    cancellationToken).ConfigureAwait(false);
                comparisons.Add(comparison);
                if (comparison.Classification is ResultClassification.Compatible or ResultClassification.FutureRegression)
                {
                    trustworthyComparison = true;
                }
                else
                {
                    hasUntrustworthyResult = true;
                }
            }

            var observations = comparisons.Select(comparison => comparison.Classification).ToArray();
            var firstBad = FindMonotonicFirstBad(ordering.OrderedCandidates, observations);
            var observedFailures = comparisons
                .Where(comparison => comparison.Classification == ResultClassification.FutureRegression)
                .Select(comparison => comparison.Candidate)
                .ToArray();
            var watchReport = new WatchReport(
                watch.Id,
                FormatKind(watch.Kind),
                watch.Package,
                watch.Candidates,
                ordering.OrderedCandidates,
                [],
                true,
                "Candidates are parsed and sorted by NuGet-style precedence. A first-bad claim is emitted only when every later ordered candidate also fails; non-monotonic evidence reports observed failing candidates instead.",
                comparisons,
                firstBad,
                observedFailures);
            watches.Add(watchReport);
            findings.AddRange(comparisons.Where(comparison => comparison.Classification != ResultClassification.Compatible));
        }

        var exitCode = findings.Any(finding => finding.Classification == ResultClassification.FutureRegression)
            ? 1
            : hasUntrustworthyResult ? 2 : 0;
        var finalReport = new RadarReport(
            RadarContract.SchemaVersion,
            RadarContract.ToolVersion,
            revision,
            configurationPath,
            watches,
            findings,
            exitCode);
        return new AnalysisResult(finalReport, trustworthyComparison);
    }

    private static async Task<CandidateComparison> CompareCandidateAsync(
        string repositoryRoot,
        RadarConfiguration configuration,
        string configurationPath,
        string revision,
        EngineValidationResult executionValidation,
        MaterializationScope scope,
        WatchConfiguration watch,
        string candidate,
        CancellationToken cancellationToken)
    {
        var findingId = $"{watch.Id}-{SanitizeFindingId(candidate)}";
        var comparisonRoot = scope.CreateComparisonDirectory(findingId);
        var stableRoot = Path.Combine(comparisonRoot, "stable");
        var candidateRoot = Path.Combine(comparisonRoot, "candidate");
        MaterializationScope.CopyRepository(repositoryRoot, stableRoot);
        MaterializationScope.CopyRepository(repositoryRoot, candidateRoot);

        var stableWorkingDirectory = Path.Combine(stableRoot, configuration.Validation.WorkingDirectory);
        var candidateWorkingDirectory = Path.Combine(candidateRoot, configuration.Validation.WorkingDirectory);
        var stable = await RunStateAsync(
            stableRoot,
            stableWorkingDirectory,
            executionValidation.Command!,
            configuration,
            candidate: null,
            feed: null,
            cancellationToken).ConfigureAwait(false);

        CandidateOverrideResult overrideResult = watch.Kind == WatchKind.NuGetPrerelease
            ? MaterializationScope.ApplyPackageOverride(candidateRoot, watch.Package!, candidate)
            : MaterializationScope.ApplySdkOverride(candidateRoot, candidate);

        RunEvidence future;
        bool candidateEvaluated;
        if (stable.Classification != "PASS")
        {
            candidateEvaluated = false;
            future = CreateNotEvaluatedEvidence(executionValidation.Command!, configuration.Validation.WorkingDirectory, "Candidate was not evaluated because the stable control did not pass.");
        }
        else if (!overrideResult.Applied)
        {
            candidateEvaluated = false;
            future = CreateExecutionFailureEvidence(executionValidation.Command!, configuration.Validation.WorkingDirectory, overrideResult.Error ?? "Candidate override could not be applied.");
        }
        else
        {
            candidateEvaluated = true;
            future = await RunStateAsync(
                candidateRoot,
                candidateWorkingDirectory,
                executionValidation.Command!,
                configuration,
                candidate,
                watch.Feed,
                cancellationToken).ConfigureAwait(false);
        }

        var preparedValidationCommand = PrepareValidationCommand(executionValidation.Command!);

        var classification = Classify(stable, future, candidateEvaluated);
        var witness = new ReproductionWitness(
            RadarContract.SchemaVersion,
            revision,
            configurationPath,
            configuration.Validation.WorkingDirectory,
            preparedValidationCommand.Display,
            future.RestoreCommand,
            watch.Package,
            candidate,
            watch.Feed,
            watch.Kind == WatchKind.SdkPreview ? candidate : null,
            future.Summary,
            future.NormalizedSignature,
            future.Fingerprint,
            $"compat-radar reproduce {findingId}");
        return new CandidateComparison(findingId, candidate, classification, candidateEvaluated, stable, future, witness);
    }

    private static async Task<RunEvidence> RunStateAsync(
        string materializedRoot,
        string workingDirectory,
        ParsedCommand validationCommand,
        RadarConfiguration configuration,
        string? candidate,
        string? feed,
        CancellationToken cancellationToken)
    {
        var command = PrepareValidationCommand(validationCommand);
        var attempts = new List<ProcessAttempt>();
        ProcessExecutionResult? restore = null;
        string? restoreCommand = null;
        if (IsDotnetCommand(validationCommand))
        {
            var restoreCommandParsed = BuildRestoreCommand(validationCommand, materializedRoot, feed);
            restoreCommand = restoreCommandParsed.Display.Replace(Path.Combine(materializedRoot, ".packages"), "<isolated-packages>", StringComparison.OrdinalIgnoreCase);
            restore = await ProcessRunner.RunAsync(
                restoreCommandParsed,
                workingDirectory,
                BuildEnvironment(materializedRoot, candidate, attempt: 0),
                TimeSpan.FromSeconds(Math.Min(configuration.Validation.TimeoutSeconds, 900)),
                cancellationToken).ConfigureAwait(false);
            if (restore.ExitCode != 0)
            {
                return new RunEvidence(
                    "INCONCLUSIVE_EXECUTION",
                    candidate is not null,
                    validationCommand.Display,
                    configuration.Validation.WorkingDirectory,
                    restoreCommand,
                    restore.Summary,
                    restore.NormalizedSignature,
                    restore.Fingerprint,
                    [ToAttempt(restore)]);
            }
        }

        for (var attempt = 1; attempt <= configuration.Policy.ConfirmationRuns; attempt++)
        {
            var result = await ProcessRunner.RunAsync(
                command,
                workingDirectory,
                BuildEnvironment(materializedRoot, candidate, attempt),
                TimeSpan.FromSeconds(configuration.Validation.TimeoutSeconds),
                cancellationToken).ConfigureAwait(false);
            attempts.Add(ToAttempt(result));
            if (result.TimedOut || result.Cancelled || result.FailureKind == "process-launch-failed")
            {
                break;
            }
        }

        var classification = attempts.All(attempt => attempt.ExitCode == 0)
            ? "PASS"
            : attempts.Any(attempt => attempt.TimedOut || attempt.Cancelled || attempt.FailureKind == "process-launch-failed")
                ? "INCONCLUSIVE_EXECUTION"
                : attempts.Any(attempt => attempt.ExitCode == 0)
                    ? "INCONCLUSIVE_FLAKY"
                    : "FAIL";
        var representative = attempts.FirstOrDefault(attempt => attempt.ExitCode != 0) ?? attempts[0];
        return new RunEvidence(
            classification,
            candidate is not null,
            command.Display,
            configuration.Validation.WorkingDirectory,
            restoreCommand,
            representative.Summary,
            representative.NormalizedSignature,
            representative.Fingerprint,
            attempts);
    }

    private static ParsedCommand PrepareValidationCommand(ParsedCommand command)
    {
        if (!SupportsMsBuildProperties(command)
            || command.Arguments.Any(argument => argument.Equals("-p:UseSharedCompilation=false", StringComparison.OrdinalIgnoreCase)))
        {
            return command;
        }

        var arguments = command.Arguments.ToList();
        var separatorIndex = arguments.FindIndex(argument => argument == "--");
        if (separatorIndex >= 0)
        {
            arguments.Insert(separatorIndex, "-p:UseSharedCompilation=false");
        }
        else
        {
            arguments.Add("-p:UseSharedCompilation=false");
        }

        return new ParsedCommand(command.FileName, arguments);
    }

    private static bool SupportsMsBuildProperties(ParsedCommand command)
    {
        if (!IsDotnetCommand(command))
        {
            return false;
        }

        var verb = command.Arguments.Count == 0 ? null : command.Arguments[0];
        return verb is "build" or "msbuild" or "pack" or "publish" or "run" or "test";
    }

    private static ParsedCommand BuildRestoreCommand(ParsedCommand validationCommand, string materializedRoot, string? feed)
    {
        var arguments = new List<string> { "restore" };
        var target = validationCommand.Arguments.FirstOrDefault(argument =>
            argument.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || argument.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || argument.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase));
        if (target is not null) arguments.Add(target);
        arguments.Add("--nologo");
        arguments.Add("--packages");
        arguments.Add(Path.Combine(materializedRoot, ".packages"));
        if (!string.IsNullOrWhiteSpace(feed))
        {
            arguments.Add("--source");
            arguments.Add(feed);
        }

        return new ParsedCommand(validationCommand.FileName, arguments);
    }

    private static Dictionary<string, string> BuildEnvironment(string materializedRoot, string? candidate, int attempt)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NUGET_PACKAGES"] = Path.Combine(materializedRoot, ".packages"),
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["KEELMATRIX_NO_TELEMETRY"] = "1",
            ["COMPATRADAR_CANDIDATE"] = candidate is null ? "0" : "1",
            ["COMPATRADAR_CANDIDATE_VERSION"] = candidate ?? "current",
            ["COMPATRADAR_ATTEMPT"] = attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    private static bool IsDotnetCommand(ParsedCommand command)
    {
        var fileName = Path.GetFileNameWithoutExtension(command.FileName);
        return fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase);
    }

    private static ResultClassification Classify(RunEvidence stable, RunEvidence future, bool candidateEvaluated)
    {
        if (!candidateEvaluated && stable.Classification is "PASS")
        {
            return ResultClassification.InconclusiveExecution;
        }

        if (stable.Classification != "PASS")
        {
            return stable.Classification is "FAIL" or "INCONCLUSIVE_FLAKY"
                ? ResultClassification.InconclusiveBaselineFailed
                : ResultClassification.InconclusiveExecution;
        }

        return future.Classification switch
        {
            "PASS" => ResultClassification.Compatible,
            "FAIL" => ResultClassification.FutureRegression,
            "INCONCLUSIVE_FLAKY" => ResultClassification.InconclusiveFlaky,
            _ => ResultClassification.InconclusiveExecution
        };
    }

    private static string FormatKind(WatchKind kind) => kind switch
    {
        WatchKind.NuGetPrerelease => "nuget-prerelease",
        WatchKind.SdkPreview => "sdk-preview",
        _ => "unsupported"
    };

    private static string SanitizeFindingId(string candidate)
    {
        var characters = candidate.Select(character => char.IsLetterOrDigit(character) || character is '-' or '.' or '_' ? character : '_').ToArray();
        return new string(characters);
    }

    private static string? FindMonotonicFirstBad(IReadOnlyList<string> candidates, ResultClassification[] classifications)
    {
        for (var index = 0; index < classifications.Length; index++)
        {
            if (classifications[index] != ResultClassification.FutureRegression)
            {
                continue;
            }

            var allPriorPass = true;
            for (var priorIndex = 0; priorIndex < index; priorIndex++)
            {
                if (classifications[priorIndex] != ResultClassification.Compatible)
                {
                    allPriorPass = false;
                    break;
                }
            }

            var allLaterFail = true;
            for (var laterIndex = index; laterIndex < classifications.Length; laterIndex++)
            {
                if (classifications[laterIndex] != ResultClassification.FutureRegression)
                {
                    allLaterFail = false;
                    break;
                }
            }

            if (allPriorPass && allLaterFail)
            {
                return candidates[index];
            }
        }

        return null;
    }

    private static ProcessAttempt ToAttempt(ProcessExecutionResult result)
    {
        return new ProcessAttempt(result.ExitCode, result.TimedOut, result.Cancelled, result.TerminationRequested, result.FailureKind, result.Summary, result.NormalizedSignature, result.Fingerprint);
    }

    private static RunEvidence CreateNotEvaluatedEvidence(ParsedCommand command, string workingDirectory, string summary)
    {
        return new RunEvidence("NOT_EVALUATED_BASELINE_FAILED", false, command.Display, workingDirectory, null, summary, "candidate-not-evaluated-baseline-failed", "candidate-not-evaluated", []);
    }

    private static RunEvidence CreateExecutionFailureEvidence(ParsedCommand command, string workingDirectory, string summary)
    {
        return new RunEvidence("INCONCLUSIVE_EXECUTION", false, command.Display, workingDirectory, null, summary, summary, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(summary)))[..16], []);
    }
}

internal static class ReportJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new ResultClassificationConverter() }
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new ResultClassificationConverter() }
    };

    public static string Serialize(RadarReport report)
    {
        return JsonSerializer.Serialize(report, Options);
    }

    public static RadarReport? Deserialize(string json)
    {
        return JsonSerializer.Deserialize<RadarReport>(json, ReadOptions);
    }

    public static string SerializeWitness(ReproductionWitness witness)
    {
        return JsonSerializer.Serialize(witness, Options);
    }
}

internal sealed class ResultClassificationConverter : JsonConverter<ResultClassification>
{
    public override ResultClassification Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.GetString() switch
        {
            "COMPATIBLE" => ResultClassification.Compatible,
            "FUTURE_REGRESSION" => ResultClassification.FutureRegression,
            "INCONCLUSIVE_BASELINE_FAILED" => ResultClassification.InconclusiveBaselineFailed,
            "INCONCLUSIVE_FLAKY" => ResultClassification.InconclusiveFlaky,
            "INCONCLUSIVE_EXECUTION" => ResultClassification.InconclusiveExecution,
            "UNSUPPORTED" => ResultClassification.Unsupported,
            _ => throw new JsonException("Unknown result classification.")
        };
    }

    public override void Write(Utf8JsonWriter writer, ResultClassification value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            ResultClassification.Compatible => "COMPATIBLE",
            ResultClassification.FutureRegression => "FUTURE_REGRESSION",
            ResultClassification.InconclusiveBaselineFailed => "INCONCLUSIVE_BASELINE_FAILED",
            ResultClassification.InconclusiveFlaky => "INCONCLUSIVE_FLAKY",
            ResultClassification.InconclusiveExecution => "INCONCLUSIVE_EXECUTION",
            ResultClassification.Unsupported => "UNSUPPORTED",
            _ => "UNSUPPORTED"
        });
    }
}
