namespace KeelMatrix.CompatRadar.Tests;

public sealed class EngineTests
{
    [Fact]
    public async Task StableAndIdenticalCandidateAreCompatibleAndDoNotMutateWorktree()
    {
        var root = TestFixture.CreateRepository("pass");
        try
        {
            TestFixture.WriteConfiguration(root, "pass", "\"1.0.0\"");
            var before = TestFixture.HashTree(root);
            var configuration = ConfigurationLoader.Load(root, "compat-radar.json").Configuration!;
            var result = await new RadarEngine().AnalyzeAsync(root, configuration, "compat-radar.json", CancellationToken.None);

            Assert.Equal(0, result.Report.ExitCode);
            Assert.Equal(ResultClassification.Compatible, result.Report.Watches[0].Comparisons[0].Classification);
            Assert.Empty(result.Report.Findings);
            Assert.Equal(before, TestFixture.HashTree(root));
        }
        finally { TestFixture.DeleteRepository(root); }
    }

    [Fact]
    public async Task StableFailureIsBaselineInconclusiveAndCandidateIsNotEvaluated()
    {
        var root = TestFixture.CreateRepository("stable-fail");
        try
        {
            TestFixture.WriteConfiguration(root, "stable-fail", "\"1.1.0\"");
            var configuration = ConfigurationLoader.Load(root, "compat-radar.json").Configuration!;
            var result = await new RadarEngine().AnalyzeAsync(root, configuration, "compat-radar.json", CancellationToken.None);
            var comparison = result.Report.Watches[0].Comparisons[0];

            Assert.Equal(2, result.Report.ExitCode);
            Assert.Equal(ResultClassification.InconclusiveBaselineFailed, comparison.Classification);
            Assert.False(comparison.CandidateEvaluated);
            Assert.Equal("NOT_EVALUATED_BASELINE_FAILED", comparison.CandidateResult.Classification);
        }
        finally { TestFixture.DeleteRepository(root); }
    }

    [Fact]
    public async Task ReproducibleCandidateFailureIsFutureRegression()
    {
        var root = TestFixture.CreateRepository("monotonic");
        try
        {
            TestFixture.WriteConfiguration(root, "monotonic", "\"1.1.0\"");
            var configuration = ConfigurationLoader.Load(root, "compat-radar.json").Configuration!;
            var result = await new RadarEngine().AnalyzeAsync(root, configuration, "compat-radar.json", CancellationToken.None);
            var comparison = result.Report.Watches[0].Comparisons[0];

            Assert.Equal(1, result.Report.ExitCode);
            Assert.Equal(ResultClassification.FutureRegression, comparison.Classification);
            Assert.Equal("1.1.0", comparison.Witness.Candidate);
            Assert.Contains("-p:UseSharedCompilation=false", comparison.Witness.ValidationCommand, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(comparison.Witness.Fingerprint));
        }
        finally { TestFixture.DeleteRepository(root); }
    }

    [Fact]
    public async Task MonotonicCandidateSequenceLocalizesFirstConfirmedFailure()
    {
        var root = TestFixture.CreateRepository("monotonic");
        try
        {
            TestFixture.WriteConfiguration(root, "monotonic", "\"1.0.0\", \"1.1.0\", \"2.0.0\"");
            var configuration = ConfigurationLoader.Load(root, "compat-radar.json").Configuration!;
            var result = await new RadarEngine().AnalyzeAsync(root, configuration, "compat-radar.json", CancellationToken.None);

            Assert.Equal(1, result.Report.ExitCode);
            Assert.Equal("1.1.0", result.Report.Watches[0].FirstConfirmedBadCandidate);
            Assert.Equal(["1.1.0", "2.0.0"], result.Report.Watches[0].ObservedFailingCandidates);
        }
        finally { TestFixture.DeleteRepository(root); }
    }

    [Fact]
    public async Task FlakyCandidateIsInconclusive()
    {
        var root = TestFixture.CreateRepository("flaky");
        try
        {
            TestFixture.WriteConfiguration(root, "flaky", "\"1.1.0\"");
            var configuration = ConfigurationLoader.Load(root, "compat-radar.json").Configuration!;
            var result = await new RadarEngine().AnalyzeAsync(root, configuration, "compat-radar.json", CancellationToken.None);

            Assert.Equal(2, result.Report.ExitCode);
            Assert.Equal(ResultClassification.InconclusiveFlaky, result.Report.Watches[0].Comparisons[0].Classification);
        }
        finally { TestFixture.DeleteRepository(root); }
    }

    [Fact]
    public async Task NonMonotonicSequenceDoesNotClaimFirstBad()
    {
        var root = TestFixture.CreateRepository("non-monotonic");
        try
        {
            TestFixture.WriteConfiguration(root, "non-monotonic", "\"1.0.0\", \"1.1.0\", \"2.0.0\"");
            var configuration = ConfigurationLoader.Load(root, "compat-radar.json").Configuration!;
            var result = await new RadarEngine().AnalyzeAsync(root, configuration, "compat-radar.json", CancellationToken.None);
            var watch = result.Report.Watches[0];

            Assert.Null(watch.FirstConfirmedBadCandidate);
            Assert.Equal(["1.1.0"], watch.ObservedFailingCandidates);
        }
        finally { TestFixture.DeleteRepository(root); }
    }

    [Fact]
    public void SanitizesSecretsAndBoundsDiagnostics()
    {
        var diagnostic = ProcessRunner.NormalizeDiagnostic("password=shh MY_SECRET=my-secret API_KEY=\"quoted-api-key\" TOKEN=abc authorization: Bearer xyz\n" + new string('x', 5000), "C:\\work");

        Assert.DoesNotContain("shh", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("my-secret", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("quoted-api-key", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("xyz", diagnostic, StringComparison.Ordinal);
        Assert.True(diagnostic.Length <= 2000);
    }

    [Fact]
    public void SanitizesSecretLikeQuotedKeysAndCredentialShapedPairs()
    {
        var diagnostic = ProcessRunner.NormalizeDiagnostic(
            "{\"privateKey\":\"private-key-value\",\"client_secret\":\"client-secret-value\",\"auth_token\":\"auth-token-value\",\"ConnectionString\":\"connection-string-value\",\"opaque\":\"fallback-opaque-value-12345\",\"message\":\"ordinary diagnostic\"}",
            "C:\\work");

        foreach (var sensitiveValue in new[] { "private-key-value", "client-secret-value", "auth-token-value", "connection-string-value", "fallback-opaque-value-12345" })
        {
            Assert.DoesNotContain(sensitiveValue, diagnostic, StringComparison.Ordinal);
        }

        Assert.Contains("ordinary diagnostic", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimeoutIsInconclusiveAndRequestsProcessTreeTermination()
    {
        var root = TestFixture.CreateRepository("timeout");
        try
        {
            TestFixture.WriteConfiguration(root, "timeout", "\"1.0.0\"", confirmationRuns: 1);
            var json = File.ReadAllText(Path.Combine(root, "compat-radar.json")).Replace("\"timeoutSeconds\": 60", "\"timeoutSeconds\": 1", StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(root, "compat-radar.json"), json);
            var configuration = ConfigurationLoader.Load(root, "compat-radar.json").Configuration!;
            var result = await new RadarEngine().AnalyzeAsync(root, configuration, "compat-radar.json", CancellationToken.None);

            Assert.Equal(2, result.Report.ExitCode);
            Assert.Equal(ResultClassification.InconclusiveExecution, result.Report.Watches[0].Comparisons[0].Classification);
            Assert.True(result.Report.Watches[0].Comparisons[0].StableControl.Attempts[0].TerminationRequested);
        }
        finally { TestFixture.DeleteRepository(root); }
    }
}
