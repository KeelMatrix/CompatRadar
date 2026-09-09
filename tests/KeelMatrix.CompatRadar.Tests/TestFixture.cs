using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;

namespace KeelMatrix.CompatRadar.Tests;

internal static class TestFixture
{
    public static string CreateRepository(string behavior)
    {
        var root = Path.Combine(Path.GetTempPath(), "compat-radar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var feed = Path.Combine(root, "feed");
        foreach (var version in new[] { "1.0.0", "1.1.0", "2.0.0" }) CreatePackage(feed, version);
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
if (behavior == "secret-diagnostic" && candidate) Console.Error.WriteLine("{(char)123}\"apiKey\": \"json-api-key\", \"password\": \"json-password\", \"access_token\": \"json-access-token\"{(char)125}");
if (behavior == "secret-diagnostic" && candidate) Environment.Exit(17);
if (behavior == "stable-fail" && !candidate) Environment.Exit(11);
    if (behavior == "monotonic" && candidate && (version == "1.1.0" || version == "2.0.0")) Environment.Exit(12);
    if (behavior == "non-monotonic" && candidate && version == "1.1.0") Environment.Exit(13);
    if (behavior == "flaky" && candidate && version == "1.1.0" && attempt == "1") Environment.Exit(14);
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

    private static void CreatePackage(string feed, string version)
    {
        Directory.CreateDirectory(feed);
        var packagePath = Path.Combine(feed, $"CompatRadar.TestDependency.{version}.nupkg");
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        AddEntry(archive, "CompatRadar.TestDependency.nuspec", $"""
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>CompatRadar.TestDependency</id>
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
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}

internal sealed class RecordingTelemetry : IUsageTelemetry
{
    public int Calls { get; private set; }
    public void RecordTrustworthyComparison() => Calls++;
}
