namespace KeelMatrix.CompatRadar.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void ValidatesWithoutExecutingCommands()
    {
        var root = TestFixture.CreateRepository("pass");
        try
        {
            TestFixture.WriteConfiguration(root, "pass", "\"1.0.0\"");
            var result = ConfigurationLoader.Load(root, "compat-radar.json");

            Assert.True(result.IsValid);
            Assert.Equal(1, result.Configuration!.Version);
            Assert.Equal(WatchKind.NuGetPrerelease, result.Configuration.Watches[0].Kind);
        }
        finally { TestFixture.DeleteRepository(root); }
    }

    [Fact]
    public void AcceptsAbsoluteConfigurationPathInsideRepository()
    {
        var root = TestFixture.CreateRepository("pass");
        try
        {
            TestFixture.WriteConfiguration(root, "pass", "\"1.0.0\"");
            var result = ConfigurationLoader.Load(root, Path.Combine(root, "compat-radar.json"));

            Assert.True(result.IsValid);
            Assert.Equal("compat-radar.json", result.Configuration!.RepositoryRelativePath);
        }
        finally { TestFixture.DeleteRepository(root); }
    }

    [Fact]
    public void RejectsMalformedAndAmbiguousCandidateDefinitions()
    {
        var root = TestFixture.CreateRepository("pass");
        try
        {
            File.WriteAllText(Path.Combine(root, "compat-radar.json"), """
{
  "version": 1,
  "control": { "sdk": "current" },
  "watch": [{ "kind": "nuget-prerelease", "package": "CompatRadar.TestDependency", "candidates": ["1.0.0", "1.0.0.0", "not-a-version"] }],
  "validation": { "command": "dotnet test", "workingDirectory": ".", "timeoutSeconds": 30 },
  "policy": { "confirmationRuns": 2 }
}
""");
            var result = ConfigurationLoader.Load(root, "compat-radar.json");

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.Contains("unparseable or duplicate", StringComparison.Ordinal));
        }
        finally { TestFixture.DeleteRepository(root); }
    }

    [Fact]
    public void RejectsWorkingDirectoryOutsideRepositoryDuringValidation()
    {
        var root = TestFixture.CreateRepository("pass");
        try
        {
            TestFixture.WriteConfiguration(root, "pass", "\"1.0.0\"");
            var configuration = ConfigurationLoader.Load(root, "compat-radar.json").Configuration! with
            {
                Validation = new ValidationConfiguration("dotnet test", "..", 30)
            };

            var result = RadarEngine.ValidateExecution(root, configuration);
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.Contains("inside the repository", StringComparison.Ordinal));
        }
        finally { TestFixture.DeleteRepository(root); }
    }

    [Fact]
    public void RejectsRuntimePreviewSelectorOutsideTheSupportedSdkBoundary()
    {
        var root = TestFixture.CreateRepository("pass");
        try
        {
            File.WriteAllText(Path.Combine(root, "compat-radar.json"), """
{
  "version": 1,
  "control": { "sdk": "current" },
  "watch": [{ "kind": "sdk-preview", "candidates": ["9.0.120"], "runtime": "runtime" }],
  "validation": { "command": "dotnet test", "workingDirectory": ".", "timeoutSeconds": 30 },
  "policy": { "confirmationRuns": 1 }
}
""");

            var result = ConfigurationLoader.Load(root, "compat-radar.json");

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.Contains("unsupported property 'runtime'", StringComparison.Ordinal));
        }
        finally { TestFixture.DeleteRepository(root); }
    }

    [Fact]
    public void SDKOverrideIsAppliedOnlyToTheMaterializedCopy()
    {
        var root = TestFixture.CreateRepository("pass");
        var copy = Path.Combine(Path.GetTempPath(), "compat-radar-sdk-test", Guid.NewGuid().ToString("N"));
        try
        {
            MaterializationScope.CopyRepository(root, copy);
            var result = MaterializationScope.ApplySdkOverride(copy, "9.0.120");

            Assert.True(result.Applied);
            Assert.Contains("9.0.120", File.ReadAllText(Path.Combine(copy, "global.json")), StringComparison.Ordinal);
            Assert.Contains("8.0.424", File.ReadAllText(Path.Combine(root, "global.json")), StringComparison.Ordinal);
        }
        finally
        {
            TestFixture.DeleteRepository(root);
            TestFixture.DeleteRepository(copy);
        }
    }
}
