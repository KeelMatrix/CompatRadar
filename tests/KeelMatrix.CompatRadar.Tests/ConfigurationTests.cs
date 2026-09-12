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
    public void RejectsWrongTypeForOptionalFeedWithoutEchoingItsValue()
    {
        var root = TestFixture.CreateRepository("pass");
        try
        {
            File.WriteAllText(Path.Combine(root, "compat-radar.json"), """
{
  "version": 1,
  "control": { "sdk": "current" },
  "watch": [{ "kind": "nuget-prerelease", "package": "CompatRadar.TestDependency", "candidates": ["1.0.0"], "feed": { "url": "https://feed.example/secret-feed" } }],
  "validation": { "command": "dotnet test", "workingDirectory": ".", "timeoutSeconds": 30 },
  "policy": { "confirmationRuns": 1 }
}
""");

            var result = ConfigurationLoader.Load(root, "compat-radar.json");

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.Contains("watch[1].feed must be a string", StringComparison.Ordinal));
            Assert.DoesNotContain("secret-feed", string.Join('\n', result.Errors), StringComparison.Ordinal);
        }
        finally { TestFixture.DeleteRepository(root); }
    }

    [Theory]
    [InlineData("https://feed.example/v3/index.json?apiKey=feed-secret")]
    [InlineData("https://feed.example/v3/index.json?client_secret=feed-secret")]
    [InlineData("https://feed.example/v3/index.json?access_token=feed-secret")]
    [InlineData("https://feed.example/v3/index.json?privateKey=feed-secret")]
    [InlineData("https://feed.example/v3/index.json?sig=feed-secret")]
    [InlineData("https://feed.example/v3/index.json?credential=feed-secret")]
    [InlineData("https://feed.example/v3/index.json?connectionString=feed-secret")]
    [InlineData("https://feed.example/v3/index.json?tenant=feed-secret")]
    [InlineData("https://feed.example/v3/index.json?protocol=feed-secret")]
    [InlineData("https://feed.example/v3/index.json?p=feed-secret&other=1")]
    [InlineData("https://feed.example/v3/index.json?=feed-secret")]
    [InlineData("https://feed.example/v3/index.json#feed-secret")]
    [InlineData("https://feed.example/v3/index.json?tenant=public#feed-secret")]
    [InlineData("https://user:feed-secret@feed.example/v3/index.json")]
    public void RejectsFeedUrlsThatCouldCarryCredentials(string feed)
    {
        var root = TestFixture.CreateRepository("pass");
        try
        {
            File.WriteAllText(Path.Combine(root, "compat-radar.json"), $$"""
{
  "version": 1,
  "control": { "sdk": "current" },
  "watch": [{ "kind": "nuget-prerelease", "package": "CompatRadar.TestDependency", "candidates": ["1.0.0"], "feed": "{{feed}}" }],
  "validation": { "command": "dotnet test", "workingDirectory": ".", "timeoutSeconds": 30 },
  "policy": { "confirmationRuns": 1 }
}
""");

            var result = ConfigurationLoader.Load(root, "compat-radar.json");

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.Contains("no user-info, query string, or fragment", StringComparison.Ordinal));
            Assert.DoesNotContain("feed-secret", string.Join('\n', result.Errors), StringComparison.Ordinal);
        }
        finally { TestFixture.DeleteRepository(root); }
    }

    [Theory]
    [InlineData("https://feed.example/v3/index.json")]
    [InlineData("https://feed.example/v3/index.json/")]
    [InlineData("https://pkgs.dev.azure.com/keelmatrix/_packaging/feed/nuget/v3/index.json")]
    public void AcceptsCredentialFreeFeedUrls(string feed)
    {
        var root = TestFixture.CreateRepository("pass");
        try
        {
            File.WriteAllText(Path.Combine(root, "compat-radar.json"), $$"""
{
  "version": 1,
  "control": { "sdk": "current" },
  "watch": [{ "kind": "nuget-prerelease", "package": "CompatRadar.TestDependency", "candidates": ["1.0.0"], "feed": "{{feed}}" }],
  "validation": { "command": "dotnet test", "workingDirectory": ".", "timeoutSeconds": 30 },
  "policy": { "confirmationRuns": 1 }
}
""");

            var result = ConfigurationLoader.Load(root, "compat-radar.json");

            Assert.True(result.IsValid);
            Assert.Equal(feed, result.Configuration!.Watches[0].Feed);
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
    public void AcceptsRuntimePreviewChannel()
    {
        var root = TestFixture.CreateRepository("pass");
        try
        {
            File.WriteAllText(Path.Combine(root, "compat-radar.json"), """
{
  "version": 1,
  "control": { "sdk": "current" },
  "watch": [{ "kind": "runtime-preview", "candidates": ["10.0.0-preview.1.12345.1"] }],
  "validation": { "command": "dotnet test", "workingDirectory": ".", "timeoutSeconds": 30 },
  "policy": { "confirmationRuns": 1 }
}
""");

            var result = ConfigurationLoader.Load(root, "compat-radar.json");

            Assert.True(result.IsValid);
            Assert.Equal(WatchKind.RuntimePreview, result.Configuration!.Watches[0].Kind);
        }
        finally { TestFixture.DeleteRepository(root); }
    }

    [Fact]
    public void RuntimePreviewRejectsBuildDisabledValidation()
    {
        var prepared = RuntimePreviewAdapter.TryPrepare(
            new ParsedCommand("dotnet", ["test", "--no-build"]),
            "10.0.12",
            out _,
            out var error);

        Assert.False(prepared);
        Assert.Contains("builds the application", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimePreviewUsesExactHostOverrideForDirectApplications()
    {
        var prepared = RuntimePreviewAdapter.TryPrepare(
            new ParsedCommand("dotnet", ["Fixture.dll", "--fx-version", "8.0.0", "--roll-forward", "Major"]),
            "10.0.12",
            out var command,
            out var error);

        Assert.True(prepared, error);
        Assert.Equal(["--fx-version", "10.0.12", "--roll-forward", "Disable", "Fixture.dll"], command!.Arguments);
    }

    [Fact]
    public void CandidateFeedIsAddedToTheIsolatedConfigWithoutReplacingExistingSources()
    {
        var root = TestFixture.CreateRepository("pass");
        var copy = Path.Combine(Path.GetTempPath(), "compat-radar-feed", Guid.NewGuid().ToString("N"));
        try
        {
            MaterializationScope.CopyRepository(root, copy);
            var configPath = Path.Combine(copy, "NuGet.Config");
            var configText = File.ReadAllText(configPath).Replace(
                "</configuration>",
                "<packageSourceMapping><clear /><packageSource key=\"fixture\"><package pattern=\"Other.*\" /></packageSource></packageSourceMapping></configuration>",
                StringComparison.Ordinal);
            File.WriteAllText(configPath, configText);
            var result = MaterializationScope.AddCandidateFeed(copy, "https://feed.example/v3/index.json", "CompatRadar.TestDependency");
            var config = File.ReadAllText(Path.Combine(copy, "NuGet.Config"));

            Assert.True(result.Applied);
            Assert.Contains("key=\"fixture\"", config, StringComparison.Ordinal);
            Assert.Contains("key=\"compat-radar-candidate\"", config, StringComparison.Ordinal);
            Assert.Contains("pattern=\"CompatRadar.TestDependency\"", config, StringComparison.Ordinal);
            Assert.Contains("pattern=\"Other.*\"", config, StringComparison.Ordinal);
            Assert.DoesNotContain("--source", config, StringComparison.Ordinal);
        }
        finally
        {
            TestFixture.DeleteRepository(root);
            TestFixture.DeleteRepository(copy);
        }
    }

    [Fact]
    public async Task CandidateFeedRestoresWatchedPackageWhileNormalSourceRemainsAvailable()
    {
        var root = TestFixture.CreateRepository("pass");
        var copy = Path.Combine(Path.GetTempPath(), "compat-radar-feed-integration", Guid.NewGuid().ToString("N"));
        var packages = Path.Combine(copy, ".packages");
        try
        {
            MaterializationScope.CopyRepository(root, copy);
            var configPath = Path.Combine(copy, "NuGet.Config");
            var configText = File.ReadAllText(configPath).Replace(
                "</configuration>",
                "<packageSourceMapping><clear /><packageSource key=\"fixture\"><package pattern=\"CompatRadar.FixtureDependency\" /></packageSource></packageSourceMapping></configuration>",
                StringComparison.Ordinal);
            File.WriteAllText(configPath, configText);
            var projectPath = Path.Combine(copy, "Fixture.csproj");
            File.WriteAllText(projectPath, File.ReadAllText(projectPath).Replace(
                "</Project>",
                "<ItemGroup><PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" /></ItemGroup></Project>",
                StringComparison.Ordinal));

            var candidateFeed = Path.Combine(copy, "candidate-feed");
            TestFixture.CreatePackage(candidateFeed, "Newtonsoft.Json", "13.0.3");
            var result = MaterializationScope.AddCandidateFeed(copy, candidateFeed, "Newtonsoft.Json");
            Assert.True(result.Applied);
            Assert.Contains("key=\"fixture\"", File.ReadAllText(configPath), StringComparison.Ordinal);

            var restore = await ProcessRunner.RunAsync(
                new ParsedCommand("dotnet", ["restore", "Fixture.csproj", "--configfile", "NuGet.Config", "--packages", packages, "--no-cache", "--nologo"]),
                copy,
                new Dictionary<string, string?>
                {
                    ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                    ["KEELMATRIX_NO_TELEMETRY"] = "1"
                },
                TimeSpan.FromSeconds(120));

            Assert.True(restore.ExitCode == 0, restore.NormalizedSignature);
            Assert.True(File.Exists(Path.Combine(packages, "newtonsoft.json", "13.0.3", "newtonsoft.json.13.0.3.nupkg")));
            Assert.True(Directory.Exists(Path.Combine(packages, "compatradar.fixturedependency", "1.0.0")));
        }
        finally
        {
            TestFixture.DeleteRepository(root);
            TestFixture.DeleteRepository(copy);
        }
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
