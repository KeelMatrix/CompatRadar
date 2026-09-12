using System.Text;
using System.Text.Json;
using KeelMatrix.Telemetry;

namespace KeelMatrix.CompatRadar;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler? handler = null;
        handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            return await RadarApplication.RunAsync(args, Directory.GetCurrentDirectory(), new SharedTelemetryReporter(), Console.Out, Console.Error, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }
}

internal interface IUsageTelemetry
{
    void RecordTrustworthyComparison();
}

internal sealed class SharedTelemetryReporter : IUsageTelemetry
{
    private Client? client;

    public void RecordTrustworthyComparison()
    {
        try
        {
            if (string.Equals(Environment.GetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY"), "1", StringComparison.Ordinal))
            {
                return;
            }

            client ??= new Client("compatradar", typeof(Program));
            client.TrackActivation();
            client.TrackHeartbeat();
        }
        catch
        {
            // Telemetry is best-effort and must never affect an analysis.
        }
    }
}

internal sealed record CliOptions(
    string Command,
    string? FindingId,
    string ConfigurationPath,
    string? ReportPath,
    string Format,
    bool ShowHelp);

internal static class RadarApplication
{
    private static readonly JsonSerializerOptions CliJsonOptions = new() { WriteIndented = true };

    public static async Task<int> RunAsync(
        string[] args,
        string currentDirectory,
        IUsageTelemetry telemetry,
        TextWriter output,
        TextWriter errorOutput,
        CancellationToken cancellationToken = default)
    {
        if (!TryParse(args, out var options, out var parseError))
        {
            errorOutput.WriteLine($"CompatRadar: {parseError}");
            errorOutput.WriteLine(Usage());
            return 2;
        }

        if (options!.ShowHelp)
        {
            await output.WriteLineAsync(Usage()).ConfigureAwait(false);
            return 0;
        }

        var repositoryRoot = RepositoryLocator.FindRoot(currentDirectory);
        if (repositoryRoot is null)
        {
            errorOutput.WriteLine("CompatRadar: could not find a repository root. Run from a directory containing .git or compat-radar.json.");
            return 2;
        }

        if (options.Command == "reproduce")
        {
            return await ReproduceAsync(repositoryRoot, options, output, errorOutput).ConfigureAwait(false);
        }

        var loaded = ConfigurationLoader.Load(repositoryRoot, options.ConfigurationPath);
        if (!loaded.IsValid)
        {
            return RenderConfigurationErrors(loaded.Errors, options.Format, output, errorOutput);
        }

        var executionValidation = RadarEngine.ValidateExecution(repositoryRoot, loaded.Configuration!);
        if (!executionValidation.IsValid)
        {
            return RenderConfigurationErrors(executionValidation.Errors, options.Format, output, errorOutput);
        }

        if (options.Command == "config validate")
        {
            await output.WriteLineAsync($"Configuration is valid (schema {loaded.Configuration!.Version}); {loaded.Configuration.Watches.Count} watch channel(s) configured.").ConfigureAwait(false);
            return 0;
        }

        try
        {
            var engine = new RadarEngine();
            var analysis = await engine.AnalyzeAsync(repositoryRoot, loaded.Configuration!, loaded.Configuration!.RepositoryRelativePath, cancellationToken).ConfigureAwait(false);
            if (options.ReportPath is not null)
            {
                var reportPath = ResolveReportPath(repositoryRoot, options.ReportPath);
                if (reportPath is null)
                {
                    errorOutput.WriteLine("CompatRadar: report path must stay inside the repository.");
                    return 2;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                await File.WriteAllTextAsync(reportPath, ReportJson.Serialize(analysis.Report), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            }

            if (options.Format == "json")
            {
                await output.WriteLineAsync(ReportJson.Serialize(analysis.Report)).ConfigureAwait(false);
            }
            else
            {
                RenderText(analysis.Report, output);
            }

            if (analysis.TrustworthyComparisonCompleted)
            {
                telemetry.RecordTrustworthyComparison();
            }

            return analysis.Report.ExitCode;
        }
        catch (OperationCanceledException)
        {
            errorOutput.WriteLine("CompatRadar: analysis cancelled; no compatibility claim was produced.");
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            errorOutput.WriteLine($"CompatRadar: analysis could not complete trustworthily ({exception.GetType().Name}).");
            return 2;
        }
    }

    private static async Task<int> ReproduceAsync(string repositoryRoot, CliOptions options, TextWriter output, TextWriter errorOutput)
    {
        var reportPath = ResolveReportPath(repositoryRoot, options.ReportPath ?? RadarContract.DefaultReportFileName);
        if (reportPath is null || !File.Exists(reportPath))
        {
            errorOutput.WriteLine("CompatRadar: the prior report was not found. Pass --report <path> to reproduce from a saved report.");
            return 2;
        }

        RadarReport? report;
        try
        {
            report = ReportJson.Deserialize(await File.ReadAllTextAsync(reportPath).ConfigureAwait(false));
        }
        catch (JsonException)
        {
            errorOutput.WriteLine("CompatRadar: the prior report is not valid CompatRadar JSON.");
            return 2;
        }

        var finding = report?.Findings.FirstOrDefault(item => item.FindingId.Equals(options.FindingId, StringComparison.Ordinal));
        if (finding is null)
        {
            errorOutput.WriteLine($"CompatRadar: finding '{options.FindingId}' was not present in the report.");
            return 2;
        }

        if (options.Format == "json")
        {
            await output.WriteLineAsync(ReportJson.SerializeWitness(finding.Witness)).ConfigureAwait(false);
        }
        else
        {
            await output.WriteLineAsync($"Reproduction for {finding.FindingId}").ConfigureAwait(false);
            await output.WriteLineAsync($"Repository revision: {finding.Witness.RepositoryRevision}").ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(finding.Witness.RepositoryContentHash))
            {
                await output.WriteLineAsync($"Repository content: {finding.Witness.RepositoryContentHash}{(finding.Witness.RepositoryWorktreeDirty ? " (working tree had uncommitted changes)" : string.Empty)}").ConfigureAwait(false);
            }

            await output.WriteLineAsync($"Validation: {finding.Witness.ValidationCommand}").ConfigureAwait(false);
            if (finding.Witness.RestoreCommand is not null) await output.WriteLineAsync($"Restore: {finding.Witness.RestoreCommand}").ConfigureAwait(false);
            await output.WriteLineAsync($"Candidate: {finding.Witness.Candidate}").ConfigureAwait(false);
            await output.WriteLineAsync($"Normalized signature: {finding.Witness.NormalizedFailureSignature}").ConfigureAwait(false);
        }

        return 0;
    }

    private static int RenderConfigurationErrors(IReadOnlyList<string> errors, string format, TextWriter output, TextWriter errorOutput)
    {
        if (format == "json")
        {
            output.WriteLine(JsonSerializer.Serialize(new { schemaVersion = RadarContract.SchemaVersion, errors }, CliJsonOptions));
        }
        else
        {
            errorOutput.WriteLine("CompatRadar configuration is invalid.");
            foreach (var error in errors) errorOutput.WriteLine($"- {error}");
        }

        return 2;
    }

    private static void RenderText(RadarReport report, TextWriter output)
    {
        output.WriteLine(report.ExitCode == 1 ? "Future compatibility regressions detected." : "Future compatibility check completed.");
        var comparisonCount = report.Watches.Sum(watch => watch.Comparisons.Count);
        var regressionCount = report.Findings.Count(finding => finding.Classification == ResultClassification.FutureRegression);
        var inconclusiveCount = report.Findings.Count(finding => finding.Classification is ResultClassification.InconclusiveBaselineFailed or ResultClassification.InconclusiveFlaky or ResultClassification.InconclusiveExecution or ResultClassification.Unsupported);
        output.WriteLine($"Stable control: {GetStableSummary(report)}");
        output.WriteLine($"Watched candidates: {comparisonCount}");
        output.WriteLine($"Confirmed future regressions: {regressionCount}");
        output.WriteLine($"Inconclusive/flaky: {inconclusiveCount}");

        foreach (var watch in report.Watches)
        {
            foreach (var finding in watch.Comparisons)
            {
                if (finding.Classification == ResultClassification.Compatible)
                {
                    continue;
                }

                output.WriteLine();
                if (finding.Classification == ResultClassification.FutureRegression)
                {
                    output.WriteLine("Future compatibility regression");
                    output.WriteLine("Control:");
                    output.WriteLine("  current stable");
                    output.WriteLine("  PASS");
                    output.WriteLine("Candidate:");
                    var candidateKind = finding.Witness.Package
                        ?? (finding.Witness.Runtime is not null ? "runtime" : "SDK");
                    output.WriteLine($"  {candidateKind} {finding.Candidate}");
                    output.WriteLine("  FAIL");
                    var isConfirmedFirstBad = watch.FirstConfirmedBadCandidate is not null
                        && string.Equals(watch.FirstConfirmedBadCandidate, finding.Candidate, StringComparison.Ordinal);
                    if (isConfirmedFirstBad)
                    {
                        output.WriteLine($"First confirmed bad candidate: {finding.Candidate}");
                    }
                    else if (watch.FirstConfirmedBadCandidate is not null)
                    {
                        output.WriteLine($"Confirmed failing candidate: {finding.Candidate}");
                    }
                    else
                    {
                        output.WriteLine($"Observed failing candidate: {finding.Candidate}");
                        output.WriteLine("No monotonic first-bad boundary is claimed for this watch channel.");
                    }

                    output.WriteLine($"Failure: {finding.CandidateResult.Summary}");
                    output.WriteLine($"Normalized signature: {finding.CandidateResult.NormalizedSignature}");
                    output.WriteLine($"Reproduce: compat-radar reproduce {finding.FindingId}");
                }
                else
                {
                    output.WriteLine($"{finding.Classification}: {finding.Candidate}");
                    output.WriteLine($"  {finding.CandidateResult.Summary}");
                }
            }
        }
    }

    private static string GetStableSummary(RadarReport report)
    {
        var stable = report.Watches.SelectMany(watch => watch.Comparisons).Select(comparison => comparison.StableControl.Classification).ToArray();
        return stable.Length > 0 && stable.All(value => value == "PASS") ? "PASS" : "INCONCLUSIVE";
    }

    private static string? ResolveReportPath(string repositoryRoot, string path)
    {
        try
        {
            var fullPath = PathUtilities.ResolveUnderRoot(repositoryRoot, path);
            return PathUtilities.IsWithinRoot(repositoryRoot, fullPath) ? fullPath : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool TryParse(string[] args, out CliOptions? options, out string? error)
    {
        options = null;
        error = null;
        if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
        {
            options = new CliOptions("", null, RadarContract.ConfigurationFileName, null, "text", true);
            return true;
        }

        var positionals = new List<string>();
        string config = RadarContract.ConfigurationFileName;
        string? report = null;
        var format = "text";
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--config":
                    if (!TryReadValue(args, ref index, out var configValue, out error)) return false;
                    config = configValue;
                    break;
                case "--report":
                    if (!TryReadValue(args, ref index, out var reportValue, out error)) return false;
                    report = reportValue;
                    break;
                case "--format":
                    if (!TryReadValue(args, ref index, out var formatValue, out error)) return false;
                    format = formatValue;
                    if (format is not ("text" or "json"))
                    {
                        error = "--format must be 'text' or 'json'.";
                        return false;
                    }
                    break;
                default:
                    if (args[index].StartsWith('-'))
                    {
                        error = $"Unknown option '{args[index]}'.";
                        return false;
                    }
                    positionals.Add(args[index]);
                    break;
            }
        }

        if (positionals.SequenceEqual(["check"]))
        {
            options = new CliOptions("check", null, config, report, format, false);
            return true;
        }

        if (positionals.SequenceEqual(["config", "validate"]))
        {
            if (report is not null)
            {
                error = "'config validate' cannot be combined with --report.";
                return false;
            }

            options = new CliOptions("config validate", null, config, report, format, false);
            return true;
        }

        if (positionals.Count == 2 && positionals[0] == "reproduce")
        {
            options = new CliOptions("reproduce", positionals[1], config, report, format, false);
            return true;
        }

        error = "Expected 'check', 'config validate', or 'reproduce <finding-id>'.";
        return false;
    }

    private static bool TryReadValue(string[] args, ref int index, out string value, out string? error)
    {
        var option = args[index];
        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            error = $"Option '{option}' requires a value.";
            return false;
        }

        index++;
        if (string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith('-'))
        {
            value = string.Empty;
            error = $"Option '{option}' requires a value.";
            return false;
        }

        value = args[index];
        error = null;
        return true;
    }

    public static string Usage() => """
Usage:
  compat-radar check [--config <path>] [--format text|json] [--report <path>]
  compat-radar config validate [--config <path>] [--format text|json]
  compat-radar reproduce <finding-id> [--report <path>] [--format text|json]

The tool runs configured repository validation commands in isolated temporary copies.
""";
}

internal static class RepositoryLocator
{
    public static string? FindRoot(string startingDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startingDirectory));
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, RadarContract.ConfigurationFileName)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
