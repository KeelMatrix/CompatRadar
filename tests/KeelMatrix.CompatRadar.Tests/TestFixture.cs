using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace KeelMatrix.CompatRadar.Tests;

/// <summary>
/// Builds disposable comparison repositories for the test suite.
///
/// The fixtures never read configuration that CompatRadar injects. Compatibility outcomes come
/// from the dependency package that is actually referenced: incompatible package versions remove
/// the API the fixture uses, so the candidate build genuinely fails. The remaining scenarios
/// exercise the repository's own validation command, which observes the resolved package or
/// runtime identity exactly as a real repository would.
/// </summary>
internal static class TestFixture
{
    public const string DependencyPackageId = "CompatRadar.FixtureDependency";
    public const string BaselineVersion = "1.0.0";

    private const string StableApiProfile = "stable-api";
    private const string ApiRemovedAfterBaselineProfile = "api-removed-after-baseline";
    private const string ApiRemovedInSecondCandidateProfile = "api-removed-in-second-candidate";

    private static readonly string[] Versions = ["1.0.0", "1.1.0", "2.0.0"];
    private static readonly Dictionary<string, string> FeedCache = new(StringComparer.Ordinal);
    private static readonly object FeedLock = new();
    private static readonly string FeedRoot = Path.Combine(Path.GetTempPath(), "compat-radar-tests", "feeds");

    static TestFixture()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteFeeds();
    }

    public static string CreateRepository(string behavior)
    {
        return CreateRepositoryCore(behavior, GetOrCreateFeed(ProfileFor(behavior)));
    }

    /// <summary>
    /// Creates a repository whose watched package restores from a caller-owned feed. Tests use this
    /// to control the exact candidate versions, including prerelease labels with mixed casing, that
    /// the shared fixture feed does not contain.
    /// </summary>
    public static string CreateRepositoryWithFeed(string behavior, string feed)
    {
        return CreateRepositoryCore(behavior, feed);
    }

    private static string CreateRepositoryCore(string behavior, string feed)
    {
        var root = Path.Combine(Path.GetTempPath(), "compat-radar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "NuGet.Config"), $"""
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="fixture" value="{feed.Replace("\\", "/", StringComparison.Ordinal)}" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
""");
        File.WriteAllText(Path.Combine(root, "global.json"), "{\n  \"sdk\": { \"version\": \"8.0.424\", \"rollForward\": \"latestPatch\", \"allowPrerelease\": false }\n}");
        File.WriteAllText(Path.Combine(root, "Fixture.csproj"), $"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <UseAppHost>false</UseAppHost>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="{DependencyPackageId}" Version="{BaselineVersion}" />
  </ItemGroup>
</Project>
""");
        File.WriteAllText(Path.Combine(root, "Program.cs"), BuildProgram(behavior));
        return root;
    }

    /// <summary>
    /// Packs one dependency package version into a caller-owned feed. <paramref name="removesApi"/>
    /// mirrors the fixture profiles: the package stops exposing the API the fixture calls, so a
    /// candidate that resolves it genuinely fails to build. Build scratch state lives beside the
    /// feed, so a caller that removes its temporary feed root removes this state too.
    /// </summary>
    public static void CreateDependencyPackage(string feed, string version, bool removesApi)
    {
        var feedRoot = Directory.GetParent(Path.GetFullPath(feed))?.FullName
            ?? throw new InvalidOperationException("The dependency feed needs an owning temporary directory.");
        var profileRoot = Path.Combine(feedRoot, "build-scratch");
        Directory.CreateDirectory(profileRoot);
        BuildDependencyPackage(profileRoot, feed, version, removesApi);
    }

    private static string ProfileFor(string behavior) => behavior switch
    {
        "monotonic" => ApiRemovedAfterBaselineProfile,
        "non-monotonic" => ApiRemovedInSecondCandidateProfile,
        _ => StableApiProfile
    };

    private static string BuildProgram(string behavior)
    {
        var usesDependencyApi = behavior is "monotonic" or "non-monotonic";
        var usedApi = usesDependencyApi
            ? "Console.WriteLine(DependencyApi.Describe());"
            : "Console.WriteLine(\"fixture pass for dependency \" + version);";
        return $$"""
using System;
using System.IO;
using System.Threading;
using CompatRadar.FixtureDependency;

var behavior = "{{behavior}}";
var version = DependencyApi.PackageVersion;
var candidateRun = !string.Equals(version, "{{BaselineVersion}}", StringComparison.Ordinal);

int NextAttempt()
{
    const string attemptFile = ".fixture-attempt";
    var attempt = File.Exists(attemptFile) && int.TryParse(File.ReadAllText(attemptFile), out var parsed) ? parsed + 1 : 1;
    File.WriteAllText(attemptFile, attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
    return attempt;
}

if (behavior == "timeout") Thread.Sleep(5000);
if (behavior == "stable-fail")
{
    Console.Error.WriteLine("error: repository validation is already failing before the dependency change");
    Environment.Exit(11);
}
if (behavior == "different-baseline" && !candidateRun)
{
    var attempt = NextAttempt();
    Console.Error.WriteLine(attempt == 1 ? "baseline-A" : "baseline-B");
    Environment.Exit(16);
}
if (behavior == "flaky" && candidateRun && NextAttempt() == 1)
{
    Console.Error.WriteLine("error: flaky repository test failed on the first attempt");
    Environment.Exit(14);
}
if (behavior == "different-failure" && candidateRun)
{
    var attempt = NextAttempt();
    Console.Error.WriteLine(attempt == 1 ? "failure-A" : "failure-B");
    Environment.Exit(15);
}
if (behavior == "different-exit" && candidateRun) Environment.Exit(NextAttempt() == 1 ? 15 : 16);
if (behavior == "secret-diagnostic" && candidateRun)
{
    Console.Error.WriteLine("MY_SECRET=my-secret-value");
    Console.Error.WriteLine("API_KEY=\"quoted-api-key\"");
    Console.Error.WriteLine("TOKEN=token-value");
    Console.Error.WriteLine("authorization: Bearer bearer-value");
    Console.Error.WriteLine("{(char)123}\"apiKey\": \"json-api-key\", \"password\": \"json-password\", \"access_token\": \"json-access-token\", \"privateKey\": \"private-key-value\", \"client_secret\": \"client-secret-value\", \"auth_token\": \"auth-token-value\", \"ConnectionString\": \"connection-string-value\", \"opaque\": \"fallback-opaque-value-12345\", \"message\": \"ordinary diagnostic\"{(char)125}");
    Environment.Exit(17);
}
if (behavior == "output-bound" && candidateRun)
{
    Console.Error.Write(new string('x', 100_000));
    Environment.Exit(17);
}

{{usedApi}}
""";
    }

    public static void WriteConfiguration(string root, string behavior, string candidates, int confirmationRuns = 2)
    {
        File.WriteAllText(Path.Combine(root, "compat-radar.json"), $$"""
{
  "version": 1,
  "control": { "sdk": "current" },
  "watch": [
    {
      "id": "package-watch",
      "kind": "nuget-prerelease",
      "package": "{{DependencyPackageId}}",
      "candidates": [{{candidates}}]
    }
  ],
  "validation": {
    "command": "dotnet run --project Fixture.csproj --no-restore --nologo",
    "workingDirectory": ".",
    "timeoutSeconds": 60
  },
  "policy": { "confirmationRuns": {{confirmationRuns}} }
}
""");
    }

    public static void WriteRuntimeConfiguration(string root, string behavior, string candidate, int confirmationRuns = 1)
    {
        File.WriteAllText(Path.Combine(root, "compat-radar.json"), $$"""
{
  "version": 1,
  "control": { "sdk": "current" },
  "watch": [
    {
      "id": "runtime-watch",
      "kind": "runtime-preview",
      "candidates": ["{{candidate}}"]
    }
  ],
  "validation": {
    "command": "dotnet run --project Fixture.csproj --no-restore --nologo",
    "workingDirectory": ".",
    "timeoutSeconds": 60
  },
  "policy": { "confirmationRuns": {{confirmationRuns}} }
}
""");

        // The repository declares the runtime major version it supports. The runtime-preview
        // channel changes the runtime that actually executes the repository, so a contract
        // violation is a genuine incompatibility for the scenario that declares one.
        var declaredRuntime = behavior == "runtime-incompatible" ? "8" : string.Empty;
        File.WriteAllText(Path.Combine(root, "runtime-contract.txt"), declaredRuntime);
        File.WriteAllText(Path.Combine(root, "Program.cs"), """
using System;
using System.IO;
using System.Runtime.InteropServices;

var contract = File.Exists("runtime-contract.txt") ? File.ReadAllText("runtime-contract.txt").Trim() : string.Empty;
var requiredMajor = contract.Length > 0 && int.TryParse(contract, out var parsed) ? parsed : 0;
var current = Environment.Version;
Console.WriteLine($"runtime-major-{current.Major}-minor-{current.Minor}");
if (requiredMajor > 0 && current.Major != requiredMajor)
{
    Console.Error.WriteLine($"error: this repository requires the .NET {requiredMajor} runtime; observed {RuntimeInformation.FrameworkDescription}");
    Environment.Exit(18);
}
""");
    }

    public static void WriteRuntimeNestedConfiguration(string root, string candidate, int confirmationRuns = 1)
    {
        WriteRuntimeConfiguration(root, "runtime-nested-control", candidate, confirmationRuns);

        var nestedDirectory = Path.Combine(root, "nested");
        Directory.CreateDirectory(nestedDirectory);
        var fixtureProjectPath = Path.Combine(root, "Fixture.csproj");
        var fixtureProject = File.ReadAllText(fixtureProjectPath).Replace("</Project>", """
  <ItemGroup>
    <Compile Remove="nested/**/*.cs" />
  </ItemGroup>
</Project>
""", StringComparison.Ordinal);
        File.WriteAllText(fixtureProjectPath, fixtureProject);
        var nestedProject = Path.Combine(nestedDirectory, "NestedRuntime.csproj");
        File.WriteAllText(nestedProject, """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <UseAppHost>false</UseAppHost>
    <EnableDefaultItems>false</EnableDefaultItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Remove="../**/*.cs" />
    <Compile Include="NestedRuntime.cs" />
  </ItemGroup>
</Project>
""");
        File.WriteAllText(Path.Combine(nestedDirectory, "NestedRuntime.cs"), """
using System;

Console.WriteLine($"nested-control-runtime-major-{Environment.Version.Major}-minor-{Environment.Version.Minor}");
if (Environment.Version.Major != 8)
{
    Console.Error.WriteLine($"error: nested control app ran on unexpected runtime {Environment.Version}");
    Environment.Exit(19);
}
""");

        var outputDirectory = Path.Combine(root, "nested-runtime");
        var result = Run("dotnet", ["build", Path.Combine("nested", "NestedRuntime.csproj"), "--configuration", "Release", "--nologo", "--output", outputDirectory], root);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Nested control runtime fixture could not be built: {result.Output}");
        }

        File.WriteAllText(Path.Combine(root, "Program.cs"), """
using System;
using System.Diagnostics;

var nested = new Process
{
    StartInfo = new ProcessStartInfo
    {
        FileName = "dotnet",
        WorkingDirectory = Environment.CurrentDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    }
};
nested.StartInfo.ArgumentList.Add("nested-runtime/NestedRuntime.dll");
nested.Start();
var standardOutput = nested.StandardOutput.ReadToEndAsync();
var standardError = nested.StandardError.ReadToEndAsync();
nested.WaitForExit();
Task.WaitAll(standardOutput, standardError);
if (nested.ExitCode != 0)
{
    Console.Error.WriteLine(standardError.Result.Trim());
    Environment.Exit(nested.ExitCode);
}

Console.WriteLine(standardOutput.Result.Trim());
""");
    }

    public static void WriteSdkConfiguration(string root, string candidateSdk, int confirmationRuns = 2)
    {
        File.WriteAllText(Path.Combine(root, "compat-radar.json"), $$"""
{
  "version": 1,
  "control": { "sdk": "current" },
  "watch": [
    {
      "id": "sdk-watch",
      "kind": "sdk-preview",
      "candidates": ["{{candidateSdk}}"]
    }
  ],
  "validation": {
    "command": "dotnet run --project Fixture.csproj --no-restore --nologo",
    "workingDirectory": ".",
    "timeoutSeconds": 60
  },
  "policy": { "confirmationRuns": {{confirmationRuns}} }
}
""");

        // The repository declares the SDK it supports. The sdk-preview channel changes the SDK
        // that actually builds the repository, so building with another SDK is a genuine
        // incompatibility for this repository. The check runs for build-time targets only, so the
        // candidate state still restores and the failure is attributed to the build, not to restore.
        File.WriteAllText(Path.Combine(root, "Directory.Build.props"), """
<Project>
  <Target Name="VerifySupportedSdk" BeforeTargets="CoreCompile">
    <Error Condition="!$(NETCoreSdkVersion.StartsWith('8.0'))" Text="error: this repository requires the .NET 8 SDK; observed SDK $(NETCoreSdkVersion)." />
  </Target>
</Project>
""");
    }

    public static string FindInstalledRuntimeVersion()
    {
        var installed = ListInstalledRuntimes()
            .Where(version => !version.StartsWith("8.", StringComparison.Ordinal))
            .OrderByDescending(version => version.Contains('-', StringComparison.Ordinal))
            .ThenByDescending(ParseVersion)
            .ToArray();

        return installed.FirstOrDefault()
            ?? throw new InvalidOperationException("A non-net8 Microsoft.NETCore.App runtime is required for runtime-preview tests.");
    }

    public static string FindInstalledSdkVersion()
    {
        var installed = ListInstalledSdks()
            .Where(version => !version.StartsWith("8.", StringComparison.Ordinal))
            .OrderByDescending(version => version.Contains('-', StringComparison.Ordinal))
            .ThenByDescending(ParseVersion)
            .ToArray();

        return installed.FirstOrDefault()
            ?? throw new InvalidOperationException("An installed SDK other than the pinned 8.0.4xx control SDK is required for sdk-preview tests.");
    }

    private static IEnumerable<string> ListInstalledRuntimes()
    {
        return RunCapture("dotnet", ["--list-runtimes"])
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("Microsoft.NETCore.App ", StringComparison.Ordinal))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
    }

    private static IEnumerable<string> ListInstalledSdks()
    {
        return RunCapture("dotnet", ["--list-sdks"])
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0])
            .Where(version => version.Length > 0);
    }

    private static string RunCapture(string fileName, IReadOnlyList<string> arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath()
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private static Version ParseVersion(string value)
    {
        var core = value.Split('-', 2)[0];
        return Version.TryParse(core, out var parsed) ? parsed : new Version(0, 0);
    }

    private static string GetOrCreateFeed(string profile)
    {
        lock (FeedLock)
        {
            if (FeedCache.TryGetValue(profile, out var cached)
                && File.Exists(Path.Combine(cached, $"{DependencyPackageId}.{Versions[^1]}.nupkg")))
            {
                return cached;
            }

            var profileRoot = Path.Combine(FeedRoot, profile);
            if (Directory.Exists(profileRoot))
            {
                Directory.Delete(profileRoot, recursive: true);
            }

            var feed = Path.Combine(profileRoot, "feed");
            Directory.CreateDirectory(feed);
            foreach (var version in Versions)
            {
                BuildDependencyPackage(profileRoot, feed, version, removesApi: RemovesApi(profile, version));
            }

            FeedCache[profile] = feed;
            return feed;
        }
    }

    private static bool RemovesApi(string profile, string version) => profile switch
    {
        ApiRemovedAfterBaselineProfile => version != BaselineVersion,
        ApiRemovedInSecondCandidateProfile => version == "1.1.0",
        _ => false
    };

    private static void BuildDependencyPackage(string profileRoot, string feed, string version, bool removesApi)
    {
        var projectDirectory = Path.Combine(profileRoot, "build", version);
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(Path.Combine(projectDirectory, "NuGet.config"), """
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
  </packageSources>
</configuration>
""");
        File.WriteAllText(Path.Combine(projectDirectory, $"{DependencyPackageId}.csproj"), $"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyName>{DependencyPackageId}</AssemblyName>
    <PackageId>{DependencyPackageId}</PackageId>
    <Version>{version}</Version>
    <Authors>KeelMatrix</Authors>
    <Description>Deterministic dependency used by the CompatRadar test suite.</Description>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <EnableNETAnalyzers>false</EnableNETAnalyzers>
    <EnforceCodeStyleInBuild>false</EnforceCodeStyleInBuild>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <IncludeSymbols>false</IncludeSymbols>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
</Project>
""");
        var api = removesApi
            ? $"""public static string DescribeV2() => "dependency {version}";"""
            : $"""public static string Describe() => "dependency {version}";""";
        File.WriteAllText(Path.Combine(projectDirectory, "Dependency.cs"), $$"""
namespace CompatRadar.FixtureDependency;

public static class DependencyApi
{
    public const string PackageVersion = "{{version}}";

    {{api}}
}
""");

        var result = Run("dotnet", ["pack", $"{DependencyPackageId}.csproj", "-c", "Release", "--nologo", "--output", feed], projectDirectory);
        if (result.ExitCode != 0 || !File.Exists(Path.Combine(feed, $"{DependencyPackageId}.{version}.nupkg")))
        {
            throw new InvalidOperationException($"Fixture dependency package {DependencyPackageId} {version} could not be built: {result.Output}");
        }
    }

    public static (int ExitCode, string Output) RunGit(string repository, IReadOnlyList<string> arguments)
    {
        return Run("git", arguments, repository);
    }

    /// <summary>
    /// Writes a metadata-only package used to exercise feed configuration and restore behavior.
    /// Compatibility scenarios use real compiled dependency packages instead.
    /// </summary>
    public static void CreatePackage(string feed, string packageId, string version)
    {
        Directory.CreateDirectory(feed);
        var packagePath = Path.Combine(feed, $"{packageId}.{version}.nupkg");
        using var archive = System.IO.Compression.ZipFile.Open(packagePath, System.IO.Compression.ZipArchiveMode.Create);
        AddEntry(archive, $"{packageId}.nuspec", $"""
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>{packageId}</id>
    <version>{version}</version>
    <authors>CompatRadar Tests</authors>
    <description>Deterministic metadata-only package used by the CompatRadar test suite.</description>
  </metadata>
</package>
""");
        AddEntry(archive, "lib/net8.0/_._", string.Empty);
    }

    private static void AddEntry(System.IO.Compression.ZipArchive archive, string name, string contents)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(contents);
    }

    private static (int ExitCode, string Output) Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        process.StartInfo.Environment["DOTNET_NOLOGO"] = "1";
        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(standardOutput, standardError);
        return (process.ExitCode, standardOutput.Result + standardError.Result);
    }

    private static void DeleteFeeds()
    {
        try
        {
            if (Directory.Exists(FeedRoot))
            {
                Directory.Delete(FeedRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of the shared fixture feed cache.
        }
    }

    public static string HashTree(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file);
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData(File.ReadAllBytes(file));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static void DeleteRepository(string root)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                if (!Directory.Exists(root)) return;

                foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(entry, FileAttributes.Normal);
                }

                File.SetAttributes(root, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
                if (!Directory.Exists(root)) return;
            }
            catch (IOException) when (attempt < 49)
            {
            }
            catch (UnauthorizedAccessException) when (attempt < 49)
            {
            }

            Thread.Sleep(100);
        }

        if (Directory.Exists(root))
        {
            throw new IOException($"Fixture repository cleanup did not complete: {root}");
        }
    }
}

internal sealed class RecordingTelemetry : IUsageTelemetry
{
    public int Calls { get; private set; }
    public void RecordTrustworthyComparison() => Calls++;
}
