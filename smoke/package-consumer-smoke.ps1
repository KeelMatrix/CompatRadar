[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath
)

$ErrorActionPreference = 'Stop'
$telemetryWasSet = Test-Path Env:KEELMATRIX_NO_TELEMETRY
$telemetryValue = $env:KEELMATRIX_NO_TELEMETRY
$env:KEELMATRIX_NO_TELEMETRY = '1'
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
New-Item -ItemType Directory -Path $toolPath, $fixture, $packages, $httpCache | Out-Null
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

try {
    dotnet tool install --tool-path $toolPath --configfile $nugetConfig KeelMatrix.CompatRadar --version $packageVersion --no-cache --ignore-failed-sources | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool install failed' }

    Set-Content -LiteralPath (Join-Path $fixture 'global.json') -Value @'
{
  "sdk": { "version": "8.0.424", "rollForward": "latestPatch", "allowPrerelease": false }
}
'@
    Set-Content -LiteralPath (Join-Path $fixture 'Fixture.csproj') -Value @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
'@
    Set-Content -LiteralPath (Join-Path $fixture 'Program.cs') -Value @'
var candidate = Environment.GetEnvironmentVariable("COMPATRADAR_CANDIDATE") == "1";
var attempt = Environment.GetEnvironmentVariable("COMPATRADAR_ATTEMPT") ?? "0";
var behavior = File.Exists("smoke-behavior.txt") ? File.ReadAllText("smoke-behavior.txt").Trim() : "";
if (behavior == "baseline") {
    Environment.Exit(18);
}
if (behavior == "flaky" && candidate && attempt == "1") {
    Environment.Exit(18);
}
if (behavior == "future-break" && candidate) {
    Console.Error.WriteLine("MY_SECRET=smoke-secret-value");
    Console.Error.WriteLine("API_KEY=\"smoke-quoted-api-key\"");
    Console.Error.WriteLine("TOKEN=smoke-token-value");
    Console.Error.WriteLine("authorization: Bearer smoke-bearer-value");
    Console.Error.WriteLine("{\"apiKey\": \"smoke-json-api-key\", \"password\": \"smoke-json-password\", \"access_token\": \"smoke-json-access-token\", \"privateKey\": \"smoke-private-key-value\", \"client_secret\": \"smoke-client-secret-value\", \"auth_token\": \"smoke-auth-token-value\", \"ConnectionString\": \"smoke-connection-string-value\", \"opaque\": \"smoke-fallback-opaque-value-12345\", \"message\": \"ordinary diagnostic\"}");
    Environment.Exit(19);
}
'@
    Set-Content -LiteralPath (Join-Path $fixture 'smoke-behavior.txt') -Value ''
    $config = Join-Path $fixture 'compat-radar.json'
    Set-Content -LiteralPath $config -Value @'
{
  "version": 1,
  "control": { "sdk": "current" },
  "watch": [{ "kind": "sdk-preview", "candidates": ["8.0.424"] }],
  "validation": { "command": "dotnet run --project Fixture.csproj --no-restore --nologo", "workingDirectory": ".", "timeoutSeconds": 120 },
  "policy": { "confirmationRuns": 1 }
}
'@
    $baseConfig = Get-Content -Raw -LiteralPath $config

    $toolName = if ($IsWindows) { 'compat-radar.exe' } else { 'compat-radar' }
    $tool = Join-Path $toolPath $toolName
    if (-not (Test-Path -LiteralPath $tool)) { throw "Installed CompatRadar tool was not found at $tool." }
    Push-Location -LiteralPath $fixture
    & $tool check --config (Split-Path -Leaf $config) --format json --report 'stable.json'
    if ($LASTEXITCODE -ne 0) { throw 'stable-identical package consumer smoke failed' }

    Set-Content -LiteralPath (Join-Path $fixture 'smoke-behavior.txt') -Value 'future-break'
    $breakOutput = (& $tool check --config (Split-Path -Leaf $config) --format json --report 'break.json' 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 1) { throw 'planted future-break package consumer smoke failed' }

    $stable = Get-Content -Raw -LiteralPath (Join-Path $fixture 'stable.json') | ConvertFrom-Json
    $breakJson = Get-Content -Raw -LiteralPath (Join-Path $fixture 'break.json')
    $break = $breakJson | ConvertFrom-Json
    if ($stable.exitCode -ne 0) { throw 'stable report exit code mismatch' }
    if ($break.exitCode -ne 1 -or $break.findings.Count -ne 1 -or $break.findings[0].classification -ne 'FUTURE_REGRESSION') { throw 'future-break report mismatch' }
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

    $credentialFeedSecret = 'smoke-feed-secret-value'
    $credentialConfig = Join-Path $fixture 'credential-feed.json'
    Set-Content -LiteralPath $credentialConfig -Value @"
{
  "version": 1,
  "control": { "sdk": "current" },
  "watch": [{ "kind": "nuget-prerelease", "package": "CompatRadar.TestDependency", "candidates": ["1.0.0"], "feed": "https://feed.example/v3/index.json?apiKey=$credentialFeedSecret" }],
  "validation": { "command": "dotnet run --project Fixture.csproj --no-restore --nologo", "workingDirectory": ".", "timeoutSeconds": 120 },
  "policy": { "confirmationRuns": 1 }
}
"@
    $credentialOutput = (& $tool config validate --config (Split-Path -Leaf $credentialConfig) --format json 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 2) { throw 'credential-bearing feed URL was accepted by the installed package' }
    if ($credentialOutput.IndexOf($credentialFeedSecret, [StringComparison]::Ordinal) -ge 0) { throw 'installed package leaked a rejected feed credential' }
    if (Test-Path -LiteralPath (Join-Path $fixture 'credential-feed-report.json')) { throw 'credential-bearing feed validation wrote a report' }

    $malformedFeedSecret = 'smoke-malformed-feed-secret'
    $malformedFeedUrl = "https://feed.example/v3/index.json?apiKey=$malformedFeedSecret"
    $malformedConfig = Join-Path $fixture 'malformed-feed.json'
    Set-Content -LiteralPath $malformedConfig -Value @"
{
  "version": 1,
  "control": { "sdk": "current" },
  "watch": [{ "kind": "nuget-prerelease", "package": "CompatRadar.TestDependency", "candidates": ["1.0.0"], "feed": { "url": "$malformedFeedUrl" } }],
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
    $baselineConfig = $baseConfig
    Set-Content -LiteralPath $config -Value $baselineConfig
    & $tool check --config (Split-Path -Leaf $config) --format json --report 'baseline.json' | Out-Host
    if ($LASTEXITCODE -ne 2) { throw 'baseline-failure package consumer smoke failed' }
    $baseline = Get-Content -Raw -LiteralPath (Join-Path $fixture 'baseline.json') | ConvertFrom-Json
    if ($baseline.exitCode -ne 2 -or $baseline.watches[0].comparisons[0].classification -ne 'INCONCLUSIVE_BASELINE_FAILED') { throw 'baseline report mismatch' }

    Set-Content -LiteralPath (Join-Path $fixture 'smoke-behavior.txt') -Value 'flaky'
    $flakyConfig = $baselineConfig.Replace('"confirmationRuns": 1', '"confirmationRuns": 2')
    Set-Content -LiteralPath $config -Value $flakyConfig
    & $tool check --config (Split-Path -Leaf $config) --format json --report 'flaky.json' | Out-Host
    if ($LASTEXITCODE -ne 2) { throw 'flaky package consumer smoke failed' }
    $flaky = Get-Content -Raw -LiteralPath (Join-Path $fixture 'flaky.json') | ConvertFrom-Json
    if ($flaky.exitCode -ne 2 -or $flaky.watches[0].comparisons[0].classification -ne 'INCONCLUSIVE_FLAKY') { throw 'flaky report mismatch' }

    Write-Host 'Package consumer smoke passed.'
}
finally {
    if ((Get-Location).Path -eq $fixture) { Pop-Location }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
    if ($telemetryWasSet) { $env:KEELMATRIX_NO_TELEMETRY = $telemetryValue } else { Remove-Item Env:KEELMATRIX_NO_TELEMETRY -ErrorAction SilentlyContinue }
}
