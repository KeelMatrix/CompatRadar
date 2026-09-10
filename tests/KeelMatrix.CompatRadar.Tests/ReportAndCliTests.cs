using System.Text.Json;

namespace KeelMatrix.CompatRadar.Tests;

public sealed class ReportAndCliTests
{
    [Fact]
    public async Task HelpAndInvalidCliShapesHaveStableStreamsAndExitCodes()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var usage = RadarApplication.Usage();
        Assert.Equal(0, await RadarApplication.RunAsync([], Directory.GetCurrentDirectory(), new RecordingTelemetry(), output, error));
        Assert.Contains("compat-radar check [--config <path>] [--format text|json] [--report <path>]", usage, StringComparison.Ordinal);
        Assert.Contains("compat-radar config validate [--config <path>] [--format text|json]", usage, StringComparison.Ordinal);
        Assert.Contains("compat-radar reproduce <finding-id> [--report <path>] [--format text|json]", usage, StringComparison.Ordinal);
        Assert.Contains(usage, output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        Assert.Equal(2, await RadarApplication.RunAsync(["unknown"], Directory.GetCurrentDirectory(), new RecordingTelemetry(), output, error));
        Assert.Contains("Expected", error.ToString(), StringComparison.Ordinal);
        Assert.Contains(usage, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, output.ToString());

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        Assert.Equal(2, await RadarApplication.RunAsync(["check", "--format"], Directory.GetCurrentDirectory(), new RecordingTelemetry(), output, error));
        Assert.Contains("requires a value", error.ToString(), StringComparison.Ordinal);
        Assert.Contains(usage, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, output.ToString());

        foreach (var invalidArguments in new[]
        {
            new[] { "check", "--unknown" },
            new[] { "check", "--format", "yaml" },
            new[] { "check", "--config" },
            new[] { "check", "--report" }
        })
        {
            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            Assert.Equal(2, await RadarApplication.RunAsync(invalidArguments, Directory.GetCurrentDirectory(), new RecordingTelemetry(), output, error));
            Assert.Contains(usage, error.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, output.ToString());
        }

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        Assert.Equal(2, await RadarApplication.RunAsync(["check", "--unknown"], Directory.GetCurrentDirectory(), new RecordingTelemetry(), output, error));
        Assert.Contains("Unknown option '--unknown'.", error.ToString(), StringComparison.Ordinal);

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        Assert.Equal(2, await RadarApplication.RunAsync(["check", "--format", "yaml"], Directory.GetCurrentDirectory(), new RecordingTelemetry(), output, error));
        Assert.Contains("--format must be 'text' or 'json'.", error.ToString(), StringComparison.Ordinal);

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        Assert.Equal(2, await RadarApplication.RunAsync(["check", "--config"], Directory.GetCurrentDirectory(), new RecordingTelemetry(), output, error));
        Assert.Contains("Option '--config' requires a value.", error.ToString(), StringComparison.Ordinal);

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        Assert.Equal(2, await RadarApplication.RunAsync(["check", "--report"], Directory.GetCurrentDirectory(), new RecordingTelemetry(), output, error));
        Assert.Contains("Option '--report' requires a value.", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReproduceRequiresAnExistingFindingId()
    {
        var root = TestFixture.CreateRepository("pass");
        try
        {
            TestFixture.WriteConfiguration(root, "pass", "\"1.0.0\"", confirmationRuns: 1);
            var missingOutput = new StringWriter();
            var missingError = new StringWriter();
            Assert.Equal(2, await RadarApplication.RunAsync(["reproduce"], root, new RecordingTelemetry(), missingOutput, missingError));
            Assert.Contains("Expected 'check', 'config validate', or 'reproduce <finding-id>'.", missingError.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, missingOutput.ToString());

            var reportOutput = new StringWriter();
            var reportError = new StringWriter();
            Assert.Equal(0, await RadarApplication.RunAsync(["check", "--report", "report.json"], root, new RecordingTelemetry(), reportOutput, reportError));
            Assert.Equal(string.Empty, reportError.ToString());

            var unknownOutput = new StringWriter();
            var unknownError = new StringWriter();
            Assert.Equal(2, await RadarApplication.RunAsync(
                ["reproduce", "missing-finding", "--report", "report.json"],
                root,
                new RecordingTelemetry(),
                unknownOutput,
                unknownError));
            Assert.Contains("finding 'missing-finding' was not present in the report", unknownError.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, unknownOutput.ToString());
        }
        finally { TestFixture.DeleteRepository(root); }
    }

    [Fact]
    public async Task SuccessfulCommandsWriteOnlyToStandardOutput()
    {
        var root = TestFixture.CreateRepository("monotonic");
        try
        {
            TestFixture.WriteConfiguration(root, "monotonic", "\"1.1.0\"", confirmationRuns: 1);

            var validateOutput = new StringWriter();
            var validateError = new StringWriter();
            Assert.Equal(0, await RadarApplication.RunAsync(["config", "validate"], root, new RecordingTelemetry(), validateOutput, validateError));
            Assert.Contains("Configuration is valid", validateOutput.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, validateError.ToString());

            var checkOutput = new StringWriter();
            var checkError = new StringWriter();
            Assert.Equal(1, await RadarApplication.RunAsync(["check", "--report", "report.json"], root, new RecordingTelemetry(), checkOutput, checkError));
            Assert.Contains("Future compatibility regressions detected.", checkOutput.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, checkError.ToString());

            var reproduceOutput = new StringWriter();
            var reproduceError = new StringWriter();
            Assert.Equal(0, await RadarApplication.RunAsync(
                ["reproduce", "package-watch-1.1.0", "--report", "report.json"],
                root,
                new RecordingTelemetry(),
                reproduceOutput,
                reproduceError));
            Assert.Contains("Reproduction for package-watch-1.1.0", reproduceOutput.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, reproduceError.ToString());
        }
        finally { TestFixture.DeleteRepository(root); }
    }

    [Fact]
    public async Task InvalidOptionCombinationsDoNotWriteReports()
    {
        var root = TestFixture.CreateRepository("pass");
        try
        {
            TestFixture.WriteConfiguration(root, "pass", "\"1.0.0\"");
            var output = new StringWriter();
            var error = new StringWriter();
            var exitCode = await RadarApplication.RunAsync(
                ["config", "validate", "--report", "invalid.json"],
                root,
                new RecordingTelemetry(),
                output,
                error);

            Assert.Equal(2, exitCode);
            Assert.Contains("cannot be combined", error.ToString(), StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(root, "invalid.json")));
        }
        finally { TestFixture.DeleteRepository(root); }
    }

    [Fact]
    public void VersionOneReportFixtureRoundTripsWithoutSchemaDrift()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "v1", "compat-radar-report.json");
        var report = ReportJson.Deserialize(File.ReadAllText(path));

        Assert.NotNull(report);
        Assert.Equal(1, report!.SchemaVersion);
        Assert.Equal(ReportJson.Serialize(report), ReportJson.Serialize(ReportJson.Deserialize(ReportJson.Serialize(report))!));
    }

    [Fact]
    public async Task JsonReportIsDeterministicAndReproduceEmitsWitness()
    {
        var root = TestFixture.CreateRepository("monotonic");
        var previousDirectory = Directory.GetCurrentDirectory();
        var oldOptOut = Environment.GetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY");
        try
        {
            TestFixture.WriteConfiguration(root, "monotonic", "\"1.1.0\"");
            Environment.SetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY", "1");
            var configuration = ConfigurationLoader.Load(root, "compat-radar.json").Configuration!;
            var engine = new RadarEngine();
            var first = await engine.AnalyzeAsync(root, configuration, "compat-radar.json", CancellationToken.None);
            var second = await engine.AnalyzeAsync(root, configuration, "compat-radar.json", CancellationToken.None);

            Assert.Equal(ReportJson.Serialize(first.Report), ReportJson.Serialize(second.Report));
            var reportPath = Path.Combine(root, "compat-radar-report.json");
            await File.WriteAllTextAsync(reportPath, ReportJson.Serialize(first.Report));
            var output = new StringWriter();
            var error = new StringWriter();
            var telemetry = new RecordingTelemetry();
            var exitCode = await RadarApplication.RunAsync(["reproduce", first.Report.Findings[0].FindingId, "--report", "compat-radar-report.json", "--format", "json"], root, telemetry, output, error);

            Assert.Equal(0, exitCode);
            Assert.Contains(first.Report.Findings[0].Witness.Fingerprint, output.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
            Environment.SetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY", oldOptOut);
            TestFixture.DeleteRepository(root);
        }
    }

    [Fact]
    public async Task CliCheckWritesReportAndRecordsTelemetryOnlyForTrustworthyResult()
    {
        var root = TestFixture.CreateRepository("pass");
        var oldOptOut = Environment.GetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY");
        try
        {
            TestFixture.WriteConfiguration(root, "pass", "\"1.0.0\"");
            Environment.SetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY", "1");
            var output = new StringWriter();
            var error = new StringWriter();
            var telemetry = new RecordingTelemetry();
            var exitCode = await RadarApplication.RunAsync(["check", "--format", "json", "--report", "report.json"], root, telemetry, output, error);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(root, "report.json")));
            Assert.Equal(1, telemetry.Calls);
            using var document = JsonDocument.Parse(output.ToString());
            Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY", oldOptOut);
            TestFixture.DeleteRepository(root);
        }
    }

    [Fact]
    public async Task MalformedOptionalFeedFailsClosedForValidationAndCheckWithoutReport()
    {
        var root = TestFixture.CreateRepository("pass");
        try
        {
            TestFixture.WriteConfiguration(root, "pass", "\"1.0.0\"");
            var configurationPath = Path.Combine(root, "compat-radar.json");
            var configuration = File.ReadAllText(configurationPath).Replace(
                "\"candidates\": [\"1.0.0\"]",
                "\"candidates\": [\"1.0.0\"], \"feed\": { \"url\": \"https://feed.example/secret-feed\" }",
                StringComparison.Ordinal);
            File.WriteAllText(configurationPath, configuration);

            var validationOutput = new StringWriter();
            var validationError = new StringWriter();
            var validationExitCode = await RadarApplication.RunAsync(
                ["config", "validate", "--format", "json"],
                root,
                new RecordingTelemetry(),
                validationOutput,
                validationError);

            Assert.Equal(2, validationExitCode);
            Assert.Contains("watch[1].feed must be a string", validationOutput.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("secret-feed", validationOutput.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, validationError.ToString());

            var reportPath = Path.Combine(root, "malformed-feed-report.json");
            var checkOutput = new StringWriter();
            var checkError = new StringWriter();
            var checkExitCode = await RadarApplication.RunAsync(
                ["check", "--format", "json", "--report", Path.GetFileName(reportPath)],
                root,
                new RecordingTelemetry(),
                checkOutput,
                checkError);

            Assert.Equal(2, checkExitCode);
            Assert.Contains("watch[1].feed must be a string", checkOutput.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("secret-feed", checkOutput.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, checkError.ToString());
            Assert.False(File.Exists(reportPath));
        }
        finally { TestFixture.DeleteRepository(root); }
    }

    [Fact]
    public async Task SecretLikeDiagnosticsNeverLeakIntoConsoleReportSignaturesOrWitness()
    {
        var root = TestFixture.CreateRepository("secret-diagnostic");
        var oldOptOut = Environment.GetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY");
        try
        {
            TestFixture.WriteConfiguration(root, "secret-diagnostic", "\"1.1.0\"", confirmationRuns: 1);
            Environment.SetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY", "1");
            var output = new StringWriter();
            var error = new StringWriter();
            var telemetry = new RecordingTelemetry();
            var exitCode = await RadarApplication.RunAsync(["check", "--format", "json", "--report", "report.json"], root, telemetry, output, error);

            Assert.Equal(1, exitCode);
            var reportJson = File.ReadAllText(Path.Combine(root, "report.json"));
            var report = ReportJson.Deserialize(reportJson)!;
            var finding = Assert.Single(report.Findings);
            Assert.Equal(ResultClassification.FutureRegression, finding.Classification);
            var console = output.ToString() + error;
            var sensitiveValues = new[]
            {
                "my-secret-value",
                "quoted-api-key",
                "token-value",
                "bearer-value",
                "json-api-key",
                "json-password",
                "json-access-token",
                "private-key-value",
                "client-secret-value",
                "auth-token-value",
                "connection-string-value",
                "fallback-opaque-value-12345"
            };

            foreach (var sensitiveValue in sensitiveValues)
            {
                Assert.DoesNotContain(sensitiveValue, console, StringComparison.Ordinal);
                Assert.DoesNotContain(sensitiveValue, reportJson, StringComparison.Ordinal);
                Assert.DoesNotContain(sensitiveValue, finding.CandidateResult.Summary, StringComparison.Ordinal);
                Assert.DoesNotContain(sensitiveValue, finding.CandidateResult.NormalizedSignature, StringComparison.Ordinal);
                Assert.DoesNotContain(sensitiveValue, finding.Witness.FocusedFailure, StringComparison.Ordinal);
                Assert.DoesNotContain(sensitiveValue, finding.Witness.NormalizedFailureSignature, StringComparison.Ordinal);
                Assert.All(finding.CandidateResult.Attempts, attempt =>
                    Assert.DoesNotContain(sensitiveValue, attempt.NormalizedSignature, StringComparison.Ordinal));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY", oldOptOut);
            TestFixture.DeleteRepository(root);
        }
    }
}
