using System.Text.Json;

namespace KeelMatrix.CompatRadar.Tests;

public sealed class ReportAndCliTests
{
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
                "json-access-token"
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
