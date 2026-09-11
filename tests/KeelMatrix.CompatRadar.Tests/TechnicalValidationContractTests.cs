using System.Text.Json;

namespace KeelMatrix.CompatRadar.Tests;

public sealed class TechnicalValidationContractTests
{
    private static readonly string[] PreviewCandidateVersions =
    [
        "11.0.100-preview.7.26381.103",
        "11.0.100-rc.1.26425.128"
    ];

    [Fact]
    public void PhaseZeroManifestContainsPinnedDiverseCorpusAndCandidates()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "scripts", "technical-validation-corpus.json")));
        var manifest = document.RootElement;

        Assert.Equal(1, manifest.GetProperty("schemaVersion").GetInt32());
        var repositories = manifest.GetProperty("repositories").EnumerateArray().ToArray();
        Assert.True(repositories.Length >= 5);
        Assert.Contains(repositories, repository => repository.GetProperty("name").GetString() == "RichardSzalay.MockHttp");
        Assert.Contains(repositories, repository => repository.GetProperty("name").GetString() == "WireMock.Net");
        Assert.Contains(repositories, repository => repository.GetProperty("name").GetString() == "Verify");
        Assert.All(repositories, repository =>
        {
            Assert.Matches("^[0-9a-f]{40}$", repository.GetProperty("ref").GetString()!);
            Assert.False(string.IsNullOrWhiteSpace(repository.GetProperty("complexityProfile").GetString()));
        });

        var candidates = manifest.GetProperty("previewCandidates").EnumerateArray().ToArray();
        Assert.True(candidates.Length >= 2);
        Assert.All(candidates, candidate =>
        {
            Assert.Contains(candidate.GetProperty("version").GetString()!, PreviewCandidateVersions);
            Assert.False(string.IsNullOrWhiteSpace(candidate.GetProperty("runtimeVersion").GetString()));
        });

        var requiredEvidence = manifest.GetProperty("requiredEvidence").EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);
        foreach (var required in new[] { "deterministic-planted-breakage-detection", "stable-control-confirmation", "flaky-classification", "monotonic-and-non-monotonic-handling", "additive-feed-behavior", "unsupported-preview-behavior", "runtime", "restore-cost", "cleanup", "unlabeled-blind-judge-pack", "independent-blind-assessment" })
        {
            Assert.Contains(required, requiredEvidence);
        }
    }

    [Fact]
    public void CurrentGateEvidenceIsGeneratedOnlyByTheCurrentCiArtifact()
    {
        var root = FindRepositoryRoot();
        Assert.False(File.Exists(Path.Combine(root, "docs", "technical-validation-gate.md")));
        Assert.False(File.Exists(Path.Combine(root, "docs", "technical-validation-gate.json")));
        var documentation = File.ReadAllText(Path.Combine(root, "docs", "technical-validation.md"));
        Assert.Contains("generated artifact", documentation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("technical-validation-corpus.json", documentation, StringComparison.Ordinal);
    }

    [Fact]
    public void PhaseZeroGroundTruthKeyIsSeparateAndRecomputable()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "scripts", "technical-validation-ground-truth.json")));
        var key = document.RootElement;

        Assert.Equal(1, key.GetProperty("schemaVersion").GetInt32());
        var planted = key.GetProperty("deterministicPlantedBreakages").EnumerateArray().ToArray();
        Assert.NotEmpty(planted);
        Assert.All(planted, testCase =>
        {
            Assert.False(string.IsNullOrWhiteSpace(testCase.GetProperty("id").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(testCase.GetProperty("testName").GetString()));
            Assert.Equal("FUTURE_REGRESSION", testCase.GetProperty("expectedClassification").GetString());
            Assert.True(testCase.GetProperty("candidateCount").GetInt32() > 0);
        });
        Assert.Equal(4, planted.Sum(testCase => testCase.GetProperty("candidateCount").GetInt32()));

        var equivalent = key.GetProperty("equivalentStates").EnumerateArray().ToArray();
        Assert.NotEmpty(equivalent);
        Assert.All(equivalent, testCase => Assert.Equal(0, testCase.GetProperty("expectedFutureRegressionCount").GetInt32()));

        var gate = File.ReadAllText(Path.Combine(root, "scripts", "technical-validation-gate.ps1"));
        Assert.Contains("Read-TestOutcomes", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("plantedBreakagesDetected = 6", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("plantedBreakagesTotal = 6", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("detectionRatePercent = 100", gate, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KeelMatrix.CompatRadar.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
