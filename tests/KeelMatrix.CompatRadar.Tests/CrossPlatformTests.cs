using System.Text;

namespace KeelMatrix.CompatRadar.Tests;

public sealed class CrossPlatformTests
{
    [Fact]
    public void ConfigurationAcceptsCrLfAndRepositoryPathsUseForwardSlashes()
    {
        var root = TestFixture.CreateRepository("pass");
        var copy = Path.Combine(Path.GetTempPath(), "compat-radar-crlf", Guid.NewGuid().ToString("N"));
        try
        {
            TestFixture.WriteConfiguration(root, "pass", "\"1.0.0\"");
            var configurationPath = Path.Combine(root, "compat-radar.json");
            File.WriteAllText(configurationPath, File.ReadAllText(configurationPath).Replace("\n", "\r\n", StringComparison.Ordinal), new UTF8Encoding(false));

            Assert.True(ConfigurationLoader.Load(root, "compat-radar.json").IsValid);

            var nestedFile = Path.Combine(root, "nested", "fixture.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(nestedFile)!);
            File.WriteAllText(nestedFile, "one\r\ntwo\r\n", new UTF8Encoding(false));
            MaterializationScope.CopyRepository(root, copy);

            Assert.Equal("nested/fixture.txt", PathUtilities.ToRepositoryRelative(root, nestedFile));
            Assert.Equal(File.ReadAllBytes(nestedFile), File.ReadAllBytes(Path.Combine(copy, "nested", "fixture.txt")));
        }
        finally
        {
            TestFixture.DeleteRepository(root);
            TestFixture.DeleteRepository(copy);
        }
    }

    [Fact]
    public void MaterializationScopeCleansItsTemporaryRoot()
    {
        var scope = MaterializationScope.Create();
        var root = scope.Root;
        Assert.True(Directory.Exists(root));

        scope.Dispose();

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void MaterializationSkipsSymbolicLinksWhenThePlatformAllowsCreatingThem()
    {
        var root = TestFixture.CreateRepository("pass");
        var copy = Path.Combine(Path.GetTempPath(), "compat-radar-links", Guid.NewGuid().ToString("N"));
        var link = Path.Combine(root, "linked-fixture.txt");
        try
        {
            try
            {
                File.CreateSymbolicLink(link, Path.Combine(root, "Program.cs"));
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return;
            }

            MaterializationScope.CopyRepository(root, copy);

            Assert.False(File.Exists(Path.Combine(copy, "linked-fixture.txt")));
        }
        finally
        {
            TestFixture.DeleteRepository(root);
            TestFixture.DeleteRepository(copy);
        }
    }

    [Fact]
    public void CaseDifferentSiblingFollowsTheActualFilesystemContainmentBoundary()
    {
        var parent = Path.Combine(Path.GetTempPath(), "compat-radar-case", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "Repository");
        var sibling = Path.Combine(parent, "repository");
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(sibling);

            var distinctDirectories = Directory.EnumerateDirectories(parent).Count() == 2;
            if (distinctDirectories)
            {
                Assert.False(PathUtilities.IsWithinRoot(root, sibling));
                Assert.False(PathUtilities.IsWithinRoot(root, Path.Combine(sibling, "compat-radar.json")));
            }
            else
            {
                Assert.True(PathUtilities.IsWithinRoot(root, sibling));
                Assert.True(PathUtilities.IsWithinRoot(root, Path.Combine(sibling, "compat-radar.json")));
            }
        }
        finally { TestFixture.DeleteRepository(parent); }
    }

    [Fact]
    public void ReparsePointPathIsNotInsideRepository()
    {
        var root = TestFixture.CreateRepository("pass");
        var linkedDirectory = Path.Combine(Path.GetTempPath(), "compat-radar-link", Guid.NewGuid().ToString("N"));
        var link = Path.Combine(root, "linked-directory");
        try
        {
            Directory.CreateDirectory(linkedDirectory);
            try
            {
                Directory.CreateSymbolicLink(link, linkedDirectory);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return;
            }

            Assert.False(PathUtilities.IsWithinRoot(root, Path.Combine(link, "compat-radar.json")));
        }
        finally
        {
            TestFixture.DeleteRepository(root);
            TestFixture.DeleteRepository(linkedDirectory);
        }
    }

    [Fact]
    public async Task CancellationRequestsProcessTreeTermination()
    {
        var root = TestFixture.CreateRepository("timeout");
        try
        {
            var environment = new Dictionary<string, string?>
            {
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_NOLOGO"] = "1"
            };
            var restore = await ProcessRunner.RunAsync(
                new ParsedCommand("dotnet", ["restore", "Fixture.csproj", "--nologo"]),
                root,
                environment,
                TimeSpan.FromSeconds(60));
            Assert.True(restore.ExitCode == 0, restore.NormalizedSignature);

            var build = await ProcessRunner.RunAsync(
                new ParsedCommand("dotnet", ["build", "Fixture.csproj", "--no-restore", "--nologo"]),
                root,
                environment,
                TimeSpan.FromSeconds(60));
            Assert.True(build.ExitCode == 0, build.NormalizedSignature);

            using var cancellation = new CancellationTokenSource();
            var runTask = ProcessRunner.RunAsync(
                new ParsedCommand("dotnet", ["exec", Path.Combine(root, "bin", "Debug", "net8.0", "Fixture.dll")]),
                root,
                environment,
                TimeSpan.FromSeconds(60),
                cancellation.Token);
            await Task.Delay(100);
            cancellation.Cancel();
            var result = await runTask;

            Assert.True(result.Cancelled);
            Assert.True(result.TerminationRequested);
        }
        finally { TestFixture.DeleteRepository(root); }
    }
}
