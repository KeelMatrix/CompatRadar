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
$root = Join-Path ([IO.Path]::GetTempPath()) ('compat-radar-consumer-' + [guid]::NewGuid().ToString('N'))
$toolPath = Join-Path $root 'tool'
$fixture = Join-Path $root 'fixture'
New-Item -ItemType Directory -Path $toolPath, $fixture | Out-Null

try {
    dotnet tool install --tool-path $toolPath --add-source (Split-Path $package) KeelMatrix.CompatRadar --version 0.1.0 --no-cache --ignore-failed-sources | Out-Host
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
if (Environment.GetEnvironmentVariable("COMPATRADAR_CANDIDATE_VERSION") == "9.0.120") {
    Console.Error.WriteLine("MY_SECRET=smoke-secret-value");
    Console.Error.WriteLine("API_KEY=\"smoke-quoted-api-key\"");
    Console.Error.WriteLine("TOKEN=smoke-token-value");
    Console.Error.WriteLine("authorization: Bearer smoke-bearer-value");
    Environment.Exit(19);
}
'@
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

    $tool = Join-Path $toolPath 'compat-radar.exe'
    Push-Location -LiteralPath $fixture
    & $tool check --config $config --format json --report (Join-Path $fixture 'stable.json')
    if ($LASTEXITCODE -ne 0) { throw 'stable-identical package consumer smoke failed' }

    (Get-Content -Raw -LiteralPath $config).Replace('"8.0.424"', '"9.0.120"') | Set-Content -LiteralPath $config
    $breakOutput = (& $tool check --config $config --format json --report (Join-Path $fixture 'break.json') 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 1) { throw 'planted future-break package consumer smoke failed' }

    $stable = Get-Content -Raw -LiteralPath (Join-Path $fixture 'stable.json') | ConvertFrom-Json
    $breakJson = Get-Content -Raw -LiteralPath (Join-Path $fixture 'break.json')
    $break = $breakJson | ConvertFrom-Json
    if ($stable.exitCode -ne 0) { throw 'stable report exit code mismatch' }
    if ($break.exitCode -ne 1 -or $break.findings.Count -ne 1) { throw 'future-break report mismatch' }
    foreach ($sensitiveValue in @('smoke-secret-value', 'smoke-quoted-api-key', 'smoke-token-value', 'smoke-bearer-value')) {
        if ($breakOutput.Contains($sensitiveValue, [StringComparison]::Ordinal)) { throw "installed package leaked $sensitiveValue to console" }
        if ($breakJson.Contains($sensitiveValue, [StringComparison]::Ordinal)) { throw "installed package leaked $sensitiveValue to report" }
    }
    Write-Host 'Package consumer smoke passed.'
}
finally {
    if ((Get-Location).Path -eq $fixture) { Pop-Location }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
    if ($telemetryWasSet) { $env:KEELMATRIX_NO_TELEMETRY = $telemetryValue } else { Remove-Item Env:KEELMATRIX_NO_TELEMETRY -ErrorAction SilentlyContinue }
}
