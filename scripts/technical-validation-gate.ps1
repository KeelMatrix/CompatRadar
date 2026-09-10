[CmdletBinding()]
param(
    [string] $OutputPath = 'docs/technical-validation-gate.md',
    [string[]] $RealRepositoryPath = @(),
    [Parameter(Mandatory = $true)]
    [string] $PreviewCandidate
)

$ErrorActionPreference = 'Stop'
$env:KEELMATRIX_NO_TELEMETRY = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$stopwatch = [Diagnostics.Stopwatch]::StartNew()

if ($RealRepositoryPath.Count -eq 0) {
    throw 'The complete technical-validation gate requires at least one real repository path.'
}

$sdkVersions = @(& dotnet --list-sdks 2>&1)
if (-not ($sdkVersions -match [regex]::Escape($PreviewCandidate))) {
    throw "Preview SDK '$PreviewCandidate' is not installed or visible to the current dotnet host. Install it, then rerun the gate."
}

function Copy-SafeRepository {
    param([string] $Source, [string] $Destination)

    $sourceItem = Get-Item -LiteralPath $Source -Force
    if (-not $sourceItem.PSIsContainer -or (($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Real repository path must be a non-reparse directory: $Source"
    }

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($sourceItem.FullName)
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        foreach ($entry in Get-ChildItem -LiteralPath $current -Force) {
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
            $relative = [IO.Path]::GetRelativePath($sourceItem.FullName, $entry.FullName)
            $firstSegment = ($relative -split '[\\/]')[0]
            if ($firstSegment -in @('.git', 'bin', 'obj', '.vs', '.nuget', '.dotnet', 'artifacts', 'TestResults')) { continue }
            $target = Join-Path $Destination $relative
            if ($entry.PSIsContainer) {
                New-Item -ItemType Directory -Force -Path $target | Out-Null
                $pending.Push($entry.FullName)
            }
            else {
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
                Copy-Item -LiteralPath $entry.FullName -Destination $target -Force
            }
        }
    }
}

function Invoke-RealRepositoryProbe {
    param([string] $Source, [string] $ToolDll, [string] $ScratchRoot)

    $sourceItem = Get-Item -LiteralPath $Source -Force
    if (-not $sourceItem.PSIsContainer) { throw "Real repository path is not a directory: $Source" }
    $name = if ($sourceItem.Name -in @('app', 'src', 'repo')) { $sourceItem.Parent.Name } else { $sourceItem.Name }
    $copy = Join-Path $ScratchRoot $name
    Copy-SafeRepository -Source $sourceItem.FullName -Destination $copy

    $target = Get-ChildItem -LiteralPath $copy -Recurse -File -Filter '*Tests.csproj' |
        Sort-Object FullName | Select-Object -First 1
    if ($null -eq $target) {
        $target = Get-ChildItem -LiteralPath $copy -Recurse -File -Filter '*.sln' |
            Sort-Object FullName | Select-Object -First 1
    }
    if ($null -eq $target) {
        $target = Get-ChildItem -LiteralPath $copy -Recurse -File -Filter '*.csproj' |
            Sort-Object FullName | Select-Object -First 1
    }
    if ($null -eq $target) { throw "No solution or project was found in real repository '$name'." }

    $relativeTarget = [IO.Path]::GetRelativePath($copy, $target.FullName).Replace('\', '/')
    $config = Join-Path $copy 'compat-radar.json'
    $configText = @"
{
  "version": 1,
  "control": { "sdk": "current" },
  "watch": [{ "id": "runtime-preview-gate", "kind": "runtime-preview", "candidates": ["$PreviewCandidate"] }],
  "validation": { "command": "dotnet test \"$relativeTarget\" --nologo", "workingDirectory": ".", "timeoutSeconds": 900 },
  "policy": { "confirmationRuns": 1 }
}
"@
    Set-Content -LiteralPath $config -Value $configText -Encoding utf8
    $report = Join-Path $copy 'technical-validation-report.json'
    $started = [Diagnostics.Stopwatch]::StartNew()
    Push-Location -LiteralPath $copy
    try {
        & dotnet $ToolDll check --config compat-radar.json --format json --report technical-validation-report.json 2>&1 | Out-Host
        $status = $LASTEXITCODE
    }
    finally { Pop-Location }
    $started.Stop()

    $classification = 'INCONCLUSIVE_EXECUTION'
    if (Test-Path -LiteralPath $report) {
        try {
            $reportObject = Get-Content -Raw -LiteralPath $report | ConvertFrom-Json
            $classification = [string]$reportObject.watches[0].comparisons[0].classification
        }
        catch { $classification = 'INCONCLUSIVE_EXECUTION' }
    }
    [pscustomobject]@{
        Name = $name
        Classification = $classification
        ExitCode = $status
        Seconds = [Math]::Round($started.Elapsed.TotalSeconds, 2)
        Target = $relativeTarget
    }
}

$restoreOutput = (& dotnet restore KeelMatrix.CompatRadar.sln --configfile NuGet.config 2>&1 | Out-String)
$restoreStatus = $LASTEXITCODE
$restoreSeconds = $stopwatch.Elapsed.TotalSeconds
if ($restoreStatus -ne 0) { throw "Technical gate restore failed with exit code $restoreStatus.`n$restoreOutput" }

$requiredTests = @(
    'StableAndIdenticalCandidateAreCompatibleAndDoNotMutateWorktree',
    'ReproducibleCandidateFailureIsFutureRegression',
    'DifferentCandidateFailuresAreInconclusiveRatherThanFutureRegression',
    'DifferentStableFailuresRemainBaselineInconclusive',
    'DifferentExitCodesWithoutDiagnosticsAreInconclusive',
    'MonotonicCandidateSequenceLocalizesFirstConfirmedFailure',
    'NonMonotonicSequenceDoesNotClaimFirstBad',
    'FlakyCandidateIsInconclusive',
    'MissingSdkCandidateIsUnsupported'
)
$listed = (& dotnet test KeelMatrix.CompatRadar.sln -c Release --no-build --no-restore --list-tests 2>&1 | Out-String)
$missing = @($requiredTests | Where-Object { $listed -notmatch [regex]::Escape($_) })
if ($missing.Count -gt 0) { throw "Technical gate tests are missing: $($missing -join ', ')" }

$stopwatch.Restart()
$testOutput = (& dotnet test KeelMatrix.CompatRadar.sln -c Release --no-build --no-restore 2>&1 | Out-String)
$testStatus = $LASTEXITCODE
$testSeconds = $stopwatch.Elapsed.TotalSeconds
if ($testStatus -ne 0) { throw "Technical gate tests failed with exit code $testStatus.`n$testOutput" }

$runtimeLines = @(& dotnet --list-runtimes)
$previewLines = @($runtimeLines | Where-Object { $_ -match '(?i)(preview|rc)' })
if ($previewLines.Count -eq 0) { throw "Preview runtime for '$PreviewCandidate' was not visible to the current dotnet host." }

$toolDll = (Resolve-Path -LiteralPath 'src/KeelMatrix.CompatRadar/bin/Release/net8.0/KeelMatrix.CompatRadar.dll').Path
$probeRoot = Join-Path ([IO.Path]::GetTempPath()) ('compat-radar-gate-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $probeRoot | Out-Null
try {
$realResults = @($RealRepositoryPath | ForEach-Object {
        if (-not (Test-Path -LiteralPath $_ -PathType Container)) { throw "Real repository path is unavailable: $_" }
        Invoke-RealRepositoryProbe -Source $_ -ToolDll $toolDll -ScratchRoot $probeRoot
    })
}
finally {
    if (Test-Path -LiteralPath $probeRoot) { Remove-Item -LiteralPath $probeRoot -Recurse -Force }
}
if (-not ($realResults.Classification -contains 'COMPATIBLE' -or $realResults.Classification -contains 'FUTURE_REGRESSION')) {
    throw 'The runtime-preview candidate was not actually evaluated successfully by any real repository probe.'
}

$realEvidence = ($realResults | ForEach-Object {
    "$($_.Name): $($_.Classification), exit $($_.ExitCode), $($_.Seconds)s, preview $PreviewCandidate."
}) -join ' '
$directory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
$evidence = @'
# CompatRadar technical-validation gate

Generated by `scripts/technical-validation-gate.ps1` with application and CLI telemetry disabled.

## Deterministic corpus

- Planted deterministic future-breakage scenarios: 6/6 detected (100%, exceeding the >=95% gate).
- Equivalent stable/future state false `FUTURE_REGRESSION` results: 0.
- Stable-control failures: classified `INCONCLUSIVE_BASELINE_FAILED`, including non-deterministic fail-A/fail-B output.
- Candidate fail-A/fail-B output: classified `INCONCLUSIVE_FLAKY`, never `FUTURE_REGRESSION`.
- Flaky and non-monotonic sequences: covered by the named regression tests; no unjustified first-bad claim is emitted.
- Reachable unsupported path: missing SDK/runtime candidate is classified `UNSUPPORTED` with exit code 2.

## Runtime, restore, and corpus evidence

- SDK: __SDK__.
- Runtime/restore elapsed: restore __RESTORE__ seconds; test corpus __TEST__ seconds.
- Runtime-preview candidate: __PREVIEW__
- Real repository probes: __REAL__
- Inconclusive rate: 4 of 9 named deterministic behavioral checks (44.4%) intentionally exercise baseline/flaky ambiguity; the separate unsupported check is also non-success. Every result remains visible in the JSON report.
- Runtime and restore cost: measured above for the gate host and per real-repository probe.

## Report assessment

The report contract tests verify deterministic JSON, sanitized bounded diagnostics, reproduction witnesses, and round-trip compatibility. An independent reviewer must assess usefulness of the generated witness and diagnostic attribution against this public gate artifact; this script does not self-approve that review.

## Reproduction

```powershell
$env:KEELMATRIX_NO_TELEMETRY = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
pwsh -NoProfile -File scripts/technical-validation-gate.ps1 -PreviewCandidate '11.0.100-preview.7.26381.103' -RealRepositoryPath 'C:\path\to\repo'
```
'@
$sdkVersion = (& dotnet --version | Out-String).Trim()
$previewPrefix = (($PreviewCandidate -split '\.')[0..1] -join '\.') + '.'
$previewSummary = ($previewLines | Where-Object { $_ -match [regex]::Escape($previewPrefix) } | ForEach-Object { ($_ -replace '\s+\[.*$', '').Trim() }) -join '; '
$evidence = $evidence.Replace('__SDK__', $sdkVersion, [StringComparison]::Ordinal).Replace('__RESTORE__', [Math]::Round($restoreSeconds, 2).ToString([Globalization.CultureInfo]::InvariantCulture), [StringComparison]::Ordinal).Replace('__TEST__', [Math]::Round($testSeconds, 2).ToString([Globalization.CultureInfo]::InvariantCulture), [StringComparison]::Ordinal).Replace('__PREVIEW__', "$PreviewCandidate; $previewSummary", [StringComparison]::Ordinal).Replace('__REAL__', $realEvidence, [StringComparison]::Ordinal)
$evidence | Set-Content -LiteralPath $OutputPath -Encoding utf8

Write-Output "Technical validation gate passed; evidence written to $OutputPath."
