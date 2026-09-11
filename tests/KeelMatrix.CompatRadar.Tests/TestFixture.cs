using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;
using System.Diagnostics;

namespace KeelMatrix.CompatRadar.Tests;

internal static class TestFixture
{
    public static string CreateRepository(string behavior)
    {
        var root = Path.Combine(Path.GetTempPath(), "compat-radar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var feed = Path.Combine(root, "feed");
        foreach (var version in new[] { "1.0.0", "1.1.0", "2.0.0" }) CreatePackage(feed, "CompatRadar.TestDependency", version);
        File.WriteAllText(Path.Combine(root, "NuGet.Config"), $"""
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="fixture" value="{feed.Replace("\\", "/", StringComparison.Ordinal)}" />
  </packageSources>
</configuration>
""");
        File.WriteAllText(Path.Combine(root, "global.json"), "{\n  \"sdk\": { \"version\": \"8.0.424\", \"rollForward\": \"latestPatch\", \"allowPrerelease\": false }\n}");
        File.WriteAllText(Path.Combine(root, "Fixture.csproj"), """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="CompatRadar.TestDependency" Version="1.0.0" />
  </ItemGroup>
</Project>
""");
        File.WriteAllText(Path.Combine(root, "Program.cs"), $"""
using System;
var candidate = Environment.GetEnvironmentVariable("COMPATRADAR_CANDIDATE") == "1";
var version = Environment.GetEnvironmentVariable("COMPATRADAR_CANDIDATE_VERSION") ?? "current";
var attempt = Environment.GetEnvironmentVariable("COMPATRADAR_ATTEMPT") ?? "0";
var behavior = "{behavior}";
if (behavior == "timeout") System.Threading.Thread.Sleep(5000);
if (behavior == "secret-diagnostic" && candidate) Console.Error.WriteLine("MY_SECRET=my-secret-value");
if (behavior == "secret-diagnostic" && candidate) Console.Error.WriteLine("API_KEY=\"quoted-api-key\"");
if (behavior == "secret-diagnostic" && candidate) Console.Error.WriteLine("TOKEN=token-value");
if (behavior == "secret-diagnostic" && candidate) Console.Error.WriteLine("authorization: Bearer bearer-value");
if (behavior == "secret-diagnostic" && candidate) Console.Error.WriteLine("{(char)123}\"apiKey\": \"json-api-key\", \"password\": \"json-password\", \"access_token\": \"json-access-token\", \"privateKey\": \"private-key-value\", \"client_secret\": \"client-secret-value\", \"auth_token\": \"auth-token-value\", \"ConnectionString\": \"connection-string-value\", \"opaque\": \"fallback-opaque-value-12345\", \"message\": \"ordinary diagnostic\"{(char)125}");
if (behavior == "secret-diagnostic" && candidate) Environment.Exit(17);
if (behavior == "output-bound" && candidate) Console.Error.Write(new string('x', 100_000));
if (behavior == "output-bound" && candidate) Environment.Exit(17);
if (behavior == "stable-fail" && !candidate) Environment.Exit(11);
    if (behavior == "monotonic" && candidate && (version == "1.1.0" || version == "2.0.0")) Environment.Exit(12);
    if (behavior == "non-monotonic" && candidate && version == "1.1.0") Environment.Exit(13);
  if (behavior == "flaky" && candidate && version == "1.1.0" && attempt == "1") Environment.Exit(14);
  if (behavior == "different-failure" && candidate) Console.Error.WriteLine(attempt == "1" ? "failure-A" : "failure-B");
  if (behavior == "different-failure" && candidate) Environment.Exit(15);
  if (behavior == "different-baseline" && !candidate) Console.Error.WriteLine(attempt == "1" ? "baseline-A" : "baseline-B");
  if (behavior == "different-baseline" && !candidate) Environment.Exit(16);
  if (behavior == "different-exit" && candidate) Environment.Exit(attempt == "1" ? 15 : 16);
""");
        return root;
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
      "package": "CompatRadar.TestDependency",
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

        File.WriteAllText(Path.Combine(root, "Program.cs"), $$"""
using System;
using System.Runtime.InteropServices;
var candidate = Environment.GetEnvironmentVariable("COMPATRADAR_CANDIDATE") == "1";
var selectedRuntime = RuntimeInformation.FrameworkDescription;
var expectedRuntime = Environment.GetEnvironmentVariable("COMPATRADAR_CANDIDATE_VERSION") ?? "";
if (candidate && !selectedRuntime.Contains(expectedRuntime, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("runtime-selection-mismatch");
    Environment.Exit(18);
}
if (candidate) Console.WriteLine("runtime-selection-confirmed");
if (candidate && "{{behavior}}" == "runtime-selection-failure") Environment.Exit(17);
""");
    }

    public static string FindInstalledRuntimeVersion()
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                ArgumentList = { "--list-runtimes" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        var installed = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("Microsoft.NETCore.App ", StringComparison.Ordinal))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1])
            .Where(version => !version.StartsWith("8.", StringComparison.Ordinal))
            .OrderByDescending(version => version.Contains('-', StringComparison.Ordinal))
            .ThenByDescending(version => ParseRuntimeVersion(version))
            .ToArray();

        return installed.FirstOrDefault()
            ?? throw new InvalidOperationException("A non-net8 Microsoft.NETCore.App runtime is required for runtime-preview tests.");
    }

    private static Version ParseRuntimeVersion(string value)
    {
        var core = value.Split('-', 2)[0];
        return Version.Parse(core);
    }

    public static void CreatePackage(string feed, string packageId, string version)
    {
        Directory.CreateDirectory(feed);
        var packagePath = Path.Combine(feed, $"{packageId}.{version}.nupkg");
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        AddEntry(archive, $"{packageId}.nuspec", $"""
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>{packageId}</id>
    <version>{version}</version>
    <authors>CompatRadar Tests</authors>
    <description>Deterministic synthetic dependency used by tests.</description>
  </metadata>
</package>
""");
        AddEntry(archive, "lib/net8.0/_._", string.Empty);
    }

    private static void AddEntry(ZipArchive archive, string name, string contents)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(contents);
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
