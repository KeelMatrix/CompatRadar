[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath
)

# Installs the built tool package in isolation and exercises the documented consumer path against a
# repository whose dependency package genuinely changes: the compatible version keeps the API the
# fixture calls, a later version changes behavior, and the newest version removes that API. The
# observed candidate failures therefore come from the dependency state, not from anything the tool
# injects into the comparison.

$ErrorActionPreference = 'Stop'
$telemetryWasSet = Test-Path Env:KEELMATRIX_NO_TELEMETRY
$telemetryValue = $env:KEELMATRIX_NO_TELEMETRY
$dotnetTelemetryWasSet = Test-Path Env:DOTNET_CLI_TELEMETRY_OPTOUT
$dotnetTelemetryValue = $env:DOTNET_CLI_TELEMETRY_OPTOUT
$noLogoWasSet = Test-Path Env:DOTNET_NOLOGO
$noLogoValue = $env:DOTNET_NOLOGO
$firstRunWasSet = Test-Path Env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE
$firstRunValue = $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE
$certificateWasSet = Test-Path Env:DOTNET_GENERATE_ASPNET_CERTIFICATE
$certificateValue = $env:DOTNET_GENERATE_ASPNET_CERTIFICATE
$env:KEELMATRIX_NO_TELEMETRY = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = '0'
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$packageName = [IO.Path]::GetFileNameWithoutExtension($package)
if ($packageName -notmatch '^KeelMatrix\.CompatRadar\.(?<version>[^.]+(?:\.[^.]+){2,3})$') { throw "Unexpected CompatRadar package name '$packageName'." }
$packageVersion = $Matches.version
$root = Join-Path ([IO.Path]::GetTempPath()) ('compat-radar-consumer-' + [guid]::NewGuid().ToString('N'))
$toolPath = Join-Path $root 'tool'
$fixture = Join-Path $root 'fixture'
$packages = Join-Path $root 'nuget-packages'
$httpCache = Join-Path $root 'nuget-http-cache'
$nugetConfig = Join-Path $root 'NuGet.Config'
$dependencyFeed = Join-Path $root 'dependency-feed'
New-Item -ItemType Directory -Path $toolPath, $fixture, $packages, $httpCache, $dependencyFeed | Out-Null
$localSource = [Security.SecurityElement]::Escape((Split-Path $package))
$nugetXml = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="fresh-local" value="$localSource" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="fresh-local">
      <package pattern="KeelMatrix.CompatRadar" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="KeelMatrix.Telemetry" />
      <package pattern="Microsoft.*" />
      <package pattern="System.*" />
      <package pattern="runtime.*" />
      <package pattern="NETStandard.Library" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@
Set-Content -LiteralPath $nugetConfig -Value $nugetXml -Encoding utf8
$env:NUGET_PACKAGES = $packages
$env:NUGET_HTTP_CACHE_PATH = $httpCache
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $root 'nuget-plugins-cache'
$env:DOTNET_CLI_HOME = Join-Path $root 'dotnet-home'
New-Item -ItemType Directory -Path $env:NUGET_PLUGINS_CACHE_PATH, $env:DOTNET_CLI_HOME | Out-Null

function New-SmokeDependencyPackage {
    param([string] $Version, [bool] $RemovesApi)

    $projectDirectory = Join-Path $root "dependency-$Version"
    New-Item -ItemType Directory -Force -Path $projectDirectory | Out-Null
    Set-Content -LiteralPath (Join-Path $projectDirectory 'NuGet.config') -Value @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
  </packageSources>
</configuration>
'@
    Set-Content -LiteralPath (Join-Path $projectDirectory 'SmokeDependency.csproj') -Value @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyName>CompatRadar.SmokeDependency</AssemblyName>
    <PackageId>CompatRadar.SmokeDependency</PackageId>
    <Version>$Version</Version>
    <Authors>KeelMatrix</Authors>
    <Description>Deterministic consumer-smoke dependency package.</Description>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <EnableNETAnalyzers>false</EnableNETAnalyzers>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <IncludeSymbols>false</IncludeSymbols>
  </PropertyGroup>
</Project>
"@
    $api = if ($RemovesApi) { 'public static string DescribeV2() => "smoke dependency" + PackageVersion;' } else { 'public static string Describe() => "smoke dependency" + PackageVersion;' }
    Set-Content -LiteralPath (Join-Path $projectDirectory 'Api.cs') -Value @"
namespace CompatRadar.SmokeDependency;

public static class SmokeApi
{
    public const string PackageVersion = "$Version";

    $api
}
"@
    & dotnet pack (Join-Path $projectDirectory 'SmokeDependency.csproj') -c Release --nologo --output $dependencyFeed | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "smoke dependency package $Version could not be built" }
    if (-not (Test-Path -LiteralPath (Join-Path $dependencyFeed "CompatRadar.SmokeDependency.$Version.nupkg"))) { throw "smoke dependency package $Version is missing" }
}

function Set-SmokeConfiguration {
    param([string] $FileName, [string] $Candidates, [int] $ConfirmationRuns = 1)

    Set-Content -LiteralPath (Join-Path $fixture $FileName) -Value @"
{
  "version": 1,
  "control": { "sdk": "current" },
  "watch": [{ "kind": "nuget-prerelease", "package": "CompatRadar.SmokeDependency", "candidates": [$Candidates] }],
  "validation": { "command": "dotnet run --project Fixture.csproj --no-restore --nologo", "workingDirectory": ".", "timeoutSeconds": 120 },
  "policy": { "confirmationRuns": $ConfirmationRuns }
}
"@
}

try {
    dotnet tool install --tool-path $toolPath --configfile $nugetConfig KeelMatrix.CompatRadar --version $packageVersion --no-cache --ignore-failed-sources | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool install failed' }

    New-SmokeDependencyPackage -Version '1.0.0' -RemovesApi $false
    New-SmokeDependencyPackage -Version '1.1.0' -RemovesApi $false
    New-SmokeDependencyPackage -Version '2.0.0' -RemovesApi $true

    Set-Content -LiteralPath (Join-Path $fixture 'global.json') -Value @'
{
  "sdk": { "version": "8.0.424", "rollForward": "latestPatch", "allowPrerelease": false }
}
'@
    Set-Content -LiteralPath (Join-Path $fixture 'NuGet.Config') -Value @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="dependency" value="$($dependencyFeed.Replace('\', '/'))" />
  </packageSources>
</configuration>
"@
    Set-Content -LiteralPath (Join-Path $fixture 'Fixture.csproj') -Value @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <UseAppHost>false</UseAppHost>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="CompatRadar.SmokeDependency" Version="1.0.0" />
  </ItemGroup>
</Project>
'@
    Set-Content -LiteralPath (Join-Path $fixture 'Program.cs') -Value @'
using System;
using System.Globalization;
using System.IO;
using CompatRadar.SmokeDependency;

var behavior = File.Exists("smoke-behavior.txt") ? File.ReadAllText("smoke-behavior.txt").Trim() : string.Empty;
var version = SmokeApi.PackageVersion;
if (behavior == "baseline")
{
    Console.Error.WriteLine("error: baseline repository validation is already failing");
    Environment.Exit(18);
}
if (behavior == "flaky" && version != "1.0.0")
{
    const string attemptFile = ".smoke-attempt";
    var attempt = File.Exists(attemptFile) && int.TryParse(File.ReadAllText(attemptFile), out var parsed) ? parsed + 1 : 1;
    File.WriteAllText(attemptFile, attempt.ToString(CultureInfo.InvariantCulture));
    if (attempt == 1)
    {
        Console.Error.WriteLine("error: flaky repository test failed on the first attempt");
        Environment.Exit(18);
    }
}
if (version != "1.0.0")
{
    Console.Error.WriteLine("MY_SECRET=smoke-secret-value");
    Console.Error.WriteLine("API_KEY=\"smoke-quoted-api-key\"");
    Console.Error.WriteLine("TOKEN=smoke-token-value");
    Console.Error.WriteLine("authorization: Bearer smoke-bearer-value");
    Console.Error.WriteLine("{\"apiKey\": \"smoke-json-api-key\", \"password\": \"smoke-json-password\", \"access_token\": \"smoke-json-access-token\", \"privateKey\": \"smoke-private-key-value\", \"client_secret\": \"smoke-client-secret-value\", \"auth_token\": \"smoke-auth-token-value\", \"ConnectionString\": \"smoke-connection-string-value\", \"opaque\": \"smoke-fallback-opaque-value-12345\", \"message\": \"ordinary diagnostic\"}");
    Environment.Exit(19);
}
Console.WriteLine("smoke fixture passed for dependency " + version);
Console.WriteLine(SmokeApi.Describe());
'@
    Set-Content -LiteralPath (Join-Path $fixture 'smoke-behavior.txt') -Value ''
    # The tool locates the repository root from the working directory, so the fixture keeps a
    # compat-radar.json at its root as a real consumer repository would.
    Set-SmokeConfiguration -FileName 'compat-radar.json' -Candidates '"1.0.0"'
    Set-SmokeConfiguration -FileName 'dependency-change.json' -Candidates '"1.1.0"'
    Set-SmokeConfiguration -FileName 'removed-api.json' -Candidates '"2.0.0"'
    Set-SmokeConfiguration -FileName 'baseline.json' -Candidates '"1.0.0"'
    Set-SmokeConfiguration -FileName 'flaky.json' -Candidates '"1.1.0"' -ConfirmationRuns 2

    $toolName = if ($IsWindows) { 'compat-radar.exe' } else { 'compat-radar' }
    $tool = Join-Path $toolPath $toolName
    if (-not (Test-Path -LiteralPath $tool)) { throw "Installed CompatRadar tool was not found at $tool." }
    Push-Location -LiteralPath $fixture
    try {
        & $tool check --config 'compat-radar.json' --format json --report 'stable.json'
        if ($LASTEXITCODE -ne 0) { throw 'stable-identical package consumer smoke failed' }
        $stable = Get-Content -Raw -LiteralPath (Join-Path $fixture 'stable.json') | ConvertFrom-Json
        if ($stable.exitCode -ne 0 -or $stable.watches[0].comparisons[0].classification -ne 'COMPATIBLE') { throw 'stable-identical report mismatch' }

        $breakOutput = (& $tool check --config 'dependency-change.json' --format json --report 'break.json' 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 1) { throw 'dependency-change package consumer smoke failed' }
        $breakJson = Get-Content -Raw -LiteralPath (Join-Path $fixture 'break.json')
        $break = $breakJson | ConvertFrom-Json
        if ($break.exitCode -ne 1 -or $break.findings.Count -ne 1 -or $break.findings[0].classification -ne 'FUTURE_REGRESSION') { throw 'dependency-change report mismatch' }

        $sensitiveValues = @(
            'smoke-secret-value',
            'smoke-quoted-api-key',
            'smoke-token-value',
            'smoke-bearer-value',
            'smoke-json-api-key',
            'smoke-json-password',
            'smoke-json-access-token',
            'smoke-private-key-value',
            'smoke-client-secret-value',
            'smoke-auth-token-value',
            'smoke-connection-string-value',
            'smoke-fallback-opaque-value-12345'
        )
        $witnessOutput = (& $tool reproduce $break.findings[0].findingId --report 'break.json' --format json 2>&1 | Out-String)
        foreach ($sensitiveValue in $sensitiveValues) {
            if ($breakOutput.IndexOf($sensitiveValue, [StringComparison]::Ordinal) -ge 0) { throw "installed package leaked $sensitiveValue to console" }
            if ($breakJson.IndexOf($sensitiveValue, [StringComparison]::Ordinal) -ge 0) { throw "installed package leaked $sensitiveValue to report" }
            if ($witnessOutput.IndexOf($sensitiveValue, [StringComparison]::Ordinal) -ge 0) { throw "installed package leaked $sensitiveValue to witness" }
            foreach ($field in @(
                [string]$break.findings[0].candidateResult.summary,
                [string]$break.findings[0].candidateResult.normalizedSignature,
                [string]$break.findings[0].witness.focusedFailure,
                [string]$break.findings[0].witness.normalizedFailureSignature
            )) {
                if ($field.IndexOf($sensitiveValue, [StringComparison]::Ordinal) -ge 0) { throw "installed package leaked $sensitiveValue to a report diagnostic field" }
            }
        }

        & $tool check --config 'removed-api.json' --format json --report 'removed-api-report.json' | Out-Host
        if ($LASTEXITCODE -ne 1) { throw 'removed-api package consumer smoke failed' }
        $removedApi = Get-Content -Raw -LiteralPath (Join-Path $fixture 'removed-api-report.json') | ConvertFrom-Json
        if ($removedApi.exitCode -ne 1 -or $removedApi.findings[0].classification -ne 'FUTURE_REGRESSION') { throw 'removed-api report mismatch' }
        if (([string]$removedApi.findings[0].candidateResult.normalizedSignature).IndexOf('does not contain a definition for', [StringComparison]::Ordinal) -lt 0) {
            throw 'removed-api failure was not a genuine dependency incompatibility'
        }

        $credentialFeedSecret = 'smoke-feed-secret-value'
        $credentialConfig = Join-Path $fixture 'credential-feed.json'
        Set-Content -LiteralPath $credentialConfig -Value @"
{
  "version": 1,
  "control": { "sdk": "current" },
  "watch": [{ "kind": "nuget-prerelease", "package": "CompatRadar.SmokeDependency", "candidates": ["1.0.0"], "feed": "https://feed.example/v3/index.json?apiKey=$credentialFeedSecret" }],
  "validation": { "command": "dotnet run --project Fixture.csproj --no-restore --nologo", "workingDirectory": ".", "timeoutSeconds": 120 },
  "policy": { "confirmationRuns": 1 }
}
"@
        $credentialOutput = (& $tool config validate --config (Split-Path -Leaf $credentialConfig) --format json 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 2) { throw 'credential-bearing feed URL was accepted by the installed package' }
        if ($credentialOutput.IndexOf($credentialFeedSecret, [StringComparison]::Ordinal) -ge 0) { throw 'installed package leaked a rejected feed credential' }
        if (Test-Path -LiteralPath (Join-Path $fixture 'credential-feed-report.json')) { throw 'credential-bearing feed validation wrote a report' }

        # Adversarial variant: the credential is hidden under a parameter name the tool cannot
        # recognize and repeated in a fragment, and the check/reproduce paths are exercised too.
        $opaqueFeedSecret = 'smoke-opaque-feed-secret-value'
        $opaqueFeedConfig = Join-Path $fixture 'opaque-feed.json'
        Set-Content -LiteralPath $opaqueFeedConfig -Value @"
{
  "version": 1,
  "control": { "sdk": "current" },
  "watch": [{ "kind": "nuget-prerelease", "package": "CompatRadar.SmokeDependency", "candidates": ["1.0.0"], "feed": "https://feed.example/v3/index.json?tenant=$opaqueFeedSecret#$opaqueFeedSecret" }],
  "validation": { "command": "dotnet run --project Fixture.csproj --no-restore --nologo", "workingDirectory": ".", "timeoutSeconds": 120 },
  "policy": { "confirmationRuns": 1 }
}
"@
        $opaqueFeedReport = Join-Path $fixture 'opaque-feed-report.json'
        $opaqueCheckOutput = (& $tool check --config (Split-Path -Leaf $opaqueFeedConfig) --format json --report (Split-Path -Leaf $opaqueFeedReport) 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 2) { throw 'a feed credential under an unrecognized parameter name or in a fragment was accepted by the installed package' }
        if ($opaqueCheckOutput.IndexOf($opaqueFeedSecret, [StringComparison]::Ordinal) -ge 0) { throw 'installed package leaked a feed credential from an unrecognized parameter name or fragment' }
        if (Test-Path -LiteralPath $opaqueFeedReport) { throw 'a rejected feed credential still produced a report' }
        $opaqueReproduceOutput = (& $tool reproduce 'package-watch-1.0.0' --config (Split-Path -Leaf $opaqueFeedConfig) --report (Split-Path -Leaf $opaqueFeedReport) 2>&1 | Out-String)
        if ($opaqueReproduceOutput.IndexOf($opaqueFeedSecret, [StringComparison]::Ordinal) -ge 0) { throw 'installed package leaked a feed credential through reproduce output' }

        $malformedFeedSecret = 'smoke-malformed-feed-secret'
        $malformedFeedUrl = "https://feed.example/v3/index.json?apiKey=$malformedFeedSecret"
        $malformedConfig = Join-Path $fixture 'malformed-feed.json'
        Set-Content -LiteralPath $malformedConfig -Value @"
{
  "version": 1,
  "control": { "sdk": "current" },
  "watch": [{ "kind": "nuget-prerelease", "package": "CompatRadar.SmokeDependency", "candidates": ["1.0.0"], "feed": { "url": "$malformedFeedUrl" } }],
  "validation": { "command": "dotnet run --project Fixture.csproj --no-restore --nologo", "workingDirectory": ".", "timeoutSeconds": 120 },
  "policy": { "confirmationRuns": 1 }
}
"@
        $malformedValidationOutput = (& $tool config validate --config (Split-Path -Leaf $malformedConfig) --format json 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 2) { throw 'wrong-typed optional feed was accepted by the installed package during config validation' }
        if ($malformedValidationOutput.IndexOf($malformedFeedSecret, [StringComparison]::Ordinal) -ge 0 -or $malformedValidationOutput.IndexOf($malformedFeedUrl, [StringComparison]::Ordinal) -ge 0) { throw 'installed package echoed a malformed feed value during config validation' }
        $malformedReport = Join-Path $fixture 'malformed-feed-report.json'
        $malformedCheckOutput = (& $tool check --config (Split-Path -Leaf $malformedConfig) --format json --report (Split-Path -Leaf $malformedReport) 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 2) { throw 'wrong-typed optional feed was accepted by the installed package during check' }
        if ($malformedCheckOutput.IndexOf($malformedFeedSecret, [StringComparison]::Ordinal) -ge 0 -or $malformedCheckOutput.IndexOf($malformedFeedUrl, [StringComparison]::Ordinal) -ge 0) { throw 'installed package echoed a malformed feed value during check' }
        if (Test-Path -LiteralPath $malformedReport) { throw 'wrong-typed optional feed check wrote a report' }

        Set-Content -LiteralPath (Join-Path $fixture 'smoke-behavior.txt') -Value 'baseline'
        & $tool check --config 'baseline.json' --format json --report 'baseline-report.json' | Out-Host
        if ($LASTEXITCODE -ne 2) { throw 'baseline-failure package consumer smoke failed' }
        $baseline = Get-Content -Raw -LiteralPath (Join-Path $fixture 'baseline-report.json') | ConvertFrom-Json
        if ($baseline.exitCode -ne 2 -or $baseline.watches[0].comparisons[0].classification -ne 'INCONCLUSIVE_BASELINE_FAILED') { throw 'baseline report mismatch' }

        Set-Content -LiteralPath (Join-Path $fixture 'smoke-behavior.txt') -Value 'flaky'
        & $tool check --config 'flaky.json' --format json --report 'flaky-report.json' | Out-Host
        if ($LASTEXITCODE -ne 2) { throw 'flaky package consumer smoke failed' }
        $flaky = Get-Content -Raw -LiteralPath (Join-Path $fixture 'flaky-report.json') | ConvertFrom-Json
        if ($flaky.exitCode -ne 2 -or $flaky.watches[0].comparisons[0].classification -ne 'INCONCLUSIVE_FLAKY') { throw 'flaky report mismatch' }
    }
    finally {
        Pop-Location
    }

    Write-Host 'Package consumer smoke passed.'
}
finally {
    if ((Get-Location).Path -eq $fixture) { Pop-Location }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
    if ($telemetryWasSet) { $env:KEELMATRIX_NO_TELEMETRY = $telemetryValue } else { Remove-Item Env:KEELMATRIX_NO_TELEMETRY -ErrorAction SilentlyContinue }
    if ($dotnetTelemetryWasSet) { $env:DOTNET_CLI_TELEMETRY_OPTOUT = $dotnetTelemetryValue } else { Remove-Item Env:DOTNET_CLI_TELEMETRY_OPTOUT -ErrorAction SilentlyContinue }
    if ($noLogoWasSet) { $env:DOTNET_NOLOGO = $noLogoValue } else { Remove-Item Env:DOTNET_NOLOGO -ErrorAction SilentlyContinue }
    if ($firstRunWasSet) { $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = $firstRunValue } else { Remove-Item Env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE -ErrorAction SilentlyContinue }
    if ($certificateWasSet) { $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = $certificateValue } else { Remove-Item Env:DOTNET_GENERATE_ASPNET_CERTIFICATE -ErrorAction SilentlyContinue }
}
