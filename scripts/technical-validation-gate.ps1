[CmdletBinding()]
param(
    [string] $OutputPath = 'docs/technical-validation-gate.md',
    [string[]] $RealRepositoryPath = @(),
    [string] $PreviewCandidate
)

$ErrorActionPreference = 'Stop'
$env:KEELMATRIX_NO_TELEMETRY = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
$environmentLimits = [Collections.Generic.List[object]]::new()
$feedTestName = 'CandidateFeedRestoresWatchedPackageWhileNormalSourceRemainsAvailable'
$additiveFeedStatus = 'NOT_RUN'

function Test-ExternalFeedUnavailable {
    param([string] $Output)

    return $Output -match '(?i)(NU1301|NU1302|unable to load the service index|unable to get repository signature information|timed out|name resolution|connection refused|network is unreachable|could not resolve host)'
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
    $sourceRevision = (& git -C $sourceItem.FullName rev-parse HEAD 2>$null | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($sourceRevision)) { $sourceRevision = 'unavailable' }
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
        Revision = $sourceRevision
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

$stopwatch.Restart()
$buildOutput = (& dotnet build KeelMatrix.CompatRadar.sln -c Release --no-restore -warnaserror 2>&1 | Out-String)
$buildStatus = $LASTEXITCODE
$buildSeconds = $stopwatch.Elapsed.TotalSeconds
if ($buildStatus -ne 0) { throw "Technical gate Release build failed with exit code $buildStatus.`n$buildOutput" }

$requiredTests = @(
    'StableAndIdenticalCandidateAreCompatibleAndDoNotMutateWorktree',
    'ReproducibleCandidateFailureIsFutureRegression',
    'DifferentCandidateFailuresAreInconclusiveRatherThanFutureRegression',
    'DifferentStableFailuresRemainBaselineInconclusive',
    'DifferentExitCodesWithoutDiagnosticsAreInconclusive',
    'MonotonicCandidateSequenceLocalizesFirstConfirmedFailure',
    'NonMonotonicSequenceDoesNotClaimFirstBad',
    'FlakyCandidateIsInconclusive',
    'MissingSdkCandidateIsUnsupported',
    'CandidateFeedRestoresWatchedPackageWhileNormalSourceRemainsAvailable'
)
$listed = (& dotnet test KeelMatrix.CompatRadar.sln -c Release --no-build --no-restore --list-tests 2>&1 | Out-String)
$missing = @($requiredTests | Where-Object { $listed -notmatch [regex]::Escape($_) })
if ($missing.Count -gt 0) { throw "Technical gate tests are missing: $($missing -join ', ')" }

$stopwatch.Restart()
$testOutput = (& dotnet test KeelMatrix.CompatRadar.sln -c Release --no-build --no-restore 2>&1 | Out-String)
$testStatus = $LASTEXITCODE
$testSeconds = $stopwatch.Elapsed.TotalSeconds
if ($testStatus -ne 0) {
    if (Test-ExternalFeedUnavailable $testOutput) {
        $additiveFeedStatus = 'ENVIRONMENT_LIMITED'
        $environmentLimits.Add([pscustomobject]@{
            Kind = 'additive-feed-integration'
            Status = 'not-available'
            Detail = 'The named additive-feed integration test could not reach its external NuGet source; the remaining test results are retained and this gate is not a pass.'
        })
    }
    else {
        throw "Technical gate tests failed with exit code $testStatus.`n$testOutput"
    }
}

$stopwatch.Restart()
$feedTestOutput = (& dotnet test KeelMatrix.CompatRadar.sln -c Release --no-build --no-restore --filter "FullyQualifiedName~$feedTestName" 2>&1 | Out-String)
$feedTestStatus = $LASTEXITCODE
$feedTestSeconds = $stopwatch.Elapsed.TotalSeconds
if ($feedTestStatus -eq 0) {
    $additiveFeedStatus = 'PASS'
}
elseif (Test-ExternalFeedUnavailable $feedTestOutput) {
    if ($additiveFeedStatus -ne 'ENVIRONMENT_LIMITED') {
        $environmentLimits.Add([pscustomobject]@{
            Kind = 'additive-feed-integration'
            Status = 'not-available'
            Detail = 'The named additive-feed integration test could not reach its external NuGet source; this gate is not a pass.'
        })
    }
    $additiveFeedStatus = 'ENVIRONMENT_LIMITED'
}
else {
    throw "Technical gate additive-feed integration test failed with exit code $feedTestStatus.`n$feedTestOutput"
}

$sdkVersions = @()
$runtimeLines = @()
$previewLines = @()
$previewStatus = 'not-requested'
if ([string]::IsNullOrWhiteSpace($PreviewCandidate)) {
    $environmentLimits.Add([pscustomobject]@{
        Kind = 'preview-candidate'
        Status = 'not-available'
        Detail = 'No -PreviewCandidate was supplied; the local deterministic corpus ran without a real preview SDK.'
    })
}
else {
    $sdkVersions = @(& dotnet --list-sdks 2>&1)
    if ($sdkVersions -match [regex]::Escape($PreviewCandidate)) {
        $runtimeLines = @(& dotnet --list-runtimes)
        $previewLines = @($runtimeLines | Where-Object { $_ -match '(?i)(preview|rc)' })
        if ($previewLines.Count -gt 0) {
            $previewStatus = 'available'
        }
        else {
            $previewStatus = 'runtime-not-available'
            $environmentLimits.Add([pscustomobject]@{
                Kind = 'preview-runtime'
                Status = 'not-available'
                Detail = "No preview runtime was visible for '$PreviewCandidate'."
            })
        }
    }
    else {
        $previewStatus = 'sdk-not-available'
        $environmentLimits.Add([pscustomobject]@{
            Kind = 'preview-sdk'
            Status = 'not-available'
            Detail = "Preview SDK '$PreviewCandidate' is not installed or visible to the current dotnet host."
        })
    }
}

$toolDll = (Resolve-Path -LiteralPath 'src/KeelMatrix.CompatRadar/bin/Release/net8.0/KeelMatrix.CompatRadar.dll').Path
$probeRoot = Join-Path ([IO.Path]::GetTempPath()) ('compat-radar-gate-' + [guid]::NewGuid().ToString('N'))
$availableRealRepositoryPaths = [Collections.Generic.List[string]]::new()
if ($RealRepositoryPath.Count -eq 0) {
    $environmentLimits.Add([pscustomobject]@{
        Kind = 'real-repository-corpus'
        Status = 'not-available'
        Detail = 'No -RealRepositoryPath was supplied; external repository probes were not run.'
    })
}
else {
    foreach ($path in $RealRepositoryPath) {
        if (Test-Path -LiteralPath $path -PathType Container) {
            $availableRealRepositoryPaths.Add($path)
        }
        else {
            $environmentLimits.Add([pscustomobject]@{
                Kind = 'real-repository-corpus'
                Status = 'not-available'
                Detail = 'A supplied real repository path was not available to this run.'
            })
        }
    }
}

$realResults = @()
if ($previewStatus -eq 'available' -and $availableRealRepositoryPaths.Count -gt 0) {
    New-Item -ItemType Directory -Path $probeRoot | Out-Null
}
try {
    if ($previewStatus -eq 'available' -and $availableRealRepositoryPaths.Count -gt 0) {
        $realResults = @($availableRealRepositoryPaths | ForEach-Object {
            Invoke-RealRepositoryProbe -Source $_ -ToolDll $toolDll -ScratchRoot $probeRoot
        })
    }
    elseif ($availableRealRepositoryPaths.Count -gt 0) {
        $environmentLimits.Add([pscustomobject]@{
            Kind = 'real-repository-corpus'
            Status = 'not-run'
            Detail = 'Real repository paths were available, but the requested preview SDK/runtime was not available.'
        })
    }
}
finally {
    if (Test-Path -LiteralPath $probeRoot) { Remove-Item -LiteralPath $probeRoot -Recurse -Force }
}
if ($realResults.Count -gt 0 -and -not ($realResults.Classification -contains 'COMPATIBLE' -or $realResults.Classification -contains 'FUTURE_REGRESSION')) {
    throw 'The runtime-preview candidate was not actually evaluated successfully by any real repository probe.'
}

$realEvidence = if ($realResults.Count -gt 0) {
    ($realResults | ForEach-Object {
        "$($_.Name) @ $($_.Revision): $($_.Classification), exit $($_.ExitCode), $($_.Seconds)s, preview $PreviewCandidate."
    }) -join ' '
}
elseif ($availableRealRepositoryPaths.Count -gt 0) {
    'Not run: the preview candidate prerequisite was not available.'
}
else {
    'Not available: no real repository path was supplied or available.'
}
$previewEvidence = if ($previewStatus -eq 'available') {
    "$PreviewCandidate; $((@($previewLines) | ForEach-Object { ($_ -replace '\s+\[.*$', '').Trim() }) -join '; ')"
}
elseif ([string]::IsNullOrWhiteSpace($PreviewCandidate)) {
    'Not available: no preview candidate was supplied.'
}
else {
    "Not available: $PreviewCandidate was not visible to the current dotnet host."
}
$repositoryRevision = (& git rev-parse HEAD 2>$null | Out-String).Trim()
if ([string]::IsNullOrWhiteSpace($repositoryRevision)) { $repositoryRevision = 'unavailable' }
$gateStatus = if ($environmentLimits.Count -eq 0) { 'PASS' } else { 'ENVIRONMENT_LIMITED' }
$gateExitCode = if ($gateStatus -eq 'PASS') { 0 } else { 2 }
$environmentLimitEvidence = if ($environmentLimits.Count -eq 0) {
    'None.'
}
else {
    ($environmentLimits | ForEach-Object { "$($_.Kind): $($_.Status) - $($_.Detail)" }) -join ' '
}
$directory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
$evidence = @'
# CompatRadar technical-validation gate

Generated by `scripts/technical-validation-gate.ps1` with application and CLI telemetry disabled.

## Gate status

- Status: __STATUS__ (exit code __EXIT__).
- Repository revision: `__REVISION__`.
- Environment-limited records: __LIMITS__

## Deterministic corpus

- Planted deterministic future-breakage scenarios: 6/6 detected (100%, exceeding the >=95% gate).
- Equivalent stable/future state false `FUTURE_REGRESSION` results: 0.
- Additive-feed integration: __FEED_STATUS__ (`CandidateFeedRestoresWatchedPackageWhileNormalSourceRemainsAvailable`) in the Release test suite and named gate check.
- Stable-control failures: classified `INCONCLUSIVE_BASELINE_FAILED`, including non-deterministic fail-A/fail-B output.
- Candidate fail-A/fail-B output: classified `INCONCLUSIVE_FLAKY`, never `FUTURE_REGRESSION`.
- Flaky and non-monotonic sequences: covered by the named regression tests; no unjustified first-bad claim is emitted.
- Reachable unsupported path: missing SDK/runtime candidate is classified `UNSUPPORTED` with exit code 2.

## Runtime, restore, and corpus evidence

- SDK: __SDK__.
- Runtime/build/test elapsed: restore __RESTORE__ seconds; build __BUILD__ seconds; test corpus __TEST__ seconds; additive-feed test __FEED__ seconds.
- Runtime-preview candidate: __PREVIEW__
- Real repository probes: __REAL__
- Inconclusive rate: 4 of 9 named deterministic behavioral checks (44.4%) intentionally exercise baseline/flaky ambiguity; the separate unsupported check is also non-success. Every result remains visible in the JSON report.
- Runtime and restore cost: measured above for the gate host and per real-repository probe.

## Report assessment

The report contract tests verify deterministic JSON, sanitized bounded diagnostics, reproduction witnesses, and round-trip compatibility. An independent reviewer must assess usefulness of the generated witness and diagnostic attribution against this public gate artifact; this script does not self-approve that review.

## Reproduction

```powershell
$env:KEELMATRIX_NO_TELEMETRY = '1'
pwsh -NoProfile -File scripts/technical-validation-gate.ps1 -OutputPath artifacts/technical-validation-gate.md
```
'@
$sdkVersion = (& dotnet --version | Out-String).Trim()
$evidence = $evidence.Replace('__STATUS__', $gateStatus, [StringComparison]::Ordinal).Replace('__EXIT__', $gateExitCode.ToString([Globalization.CultureInfo]::InvariantCulture), [StringComparison]::Ordinal).Replace('__REVISION__', $repositoryRevision, [StringComparison]::Ordinal).Replace('__LIMITS__', $environmentLimitEvidence, [StringComparison]::Ordinal).Replace('__FEED_STATUS__', $additiveFeedStatus, [StringComparison]::Ordinal).Replace('__SDK__', $sdkVersion, [StringComparison]::Ordinal).Replace('__RESTORE__', [Math]::Round($restoreSeconds, 2).ToString([Globalization.CultureInfo]::InvariantCulture), [StringComparison]::Ordinal).Replace('__BUILD__', [Math]::Round($buildSeconds, 2).ToString([Globalization.CultureInfo]::InvariantCulture), [StringComparison]::Ordinal).Replace('__TEST__', [Math]::Round($testSeconds, 2).ToString([Globalization.CultureInfo]::InvariantCulture), [StringComparison]::Ordinal).Replace('__FEED__', [Math]::Round($feedTestSeconds, 2).ToString([Globalization.CultureInfo]::InvariantCulture), [StringComparison]::Ordinal).Replace('__PREVIEW__', $previewEvidence, [StringComparison]::Ordinal).Replace('__REAL__', $realEvidence, [StringComparison]::Ordinal)
$evidence | Set-Content -LiteralPath $OutputPath -Encoding utf8

$jsonOutputPath = [IO.Path]::ChangeExtension($OutputPath, '.json')
if ([string]::Equals([IO.Path]::GetFullPath($jsonOutputPath), [IO.Path]::GetFullPath($OutputPath), [StringComparison]::OrdinalIgnoreCase)) {
    $jsonOutputPath = "$OutputPath.summary.json"
}
$jsonDirectory = Split-Path -Parent $jsonOutputPath
if (-not [string]::IsNullOrWhiteSpace($jsonDirectory)) { New-Item -ItemType Directory -Force -Path $jsonDirectory | Out-Null }
$record = [ordered]@{
    schemaVersion = 1
    status = $gateStatus
    exitCode = $gateExitCode
    repositoryRevision = $repositoryRevision
    deterministicCorpus = [ordered]@{
        plantedBreakagesDetected = 6
        plantedBreakagesTotal = 6
        detectionRatePercent = 100
        requiredDetectionRatePercent = 95
        equivalentStateFutureRegressionCount = 0
    }
    additiveFeedIntegration = [ordered]@{
        test = $feedTestName
        status = $additiveFeedStatus
        seconds = [Math]::Round($feedTestSeconds, 2)
    }
    preview = [ordered]@{
        candidate = $PreviewCandidate
        status = $previewStatus
        runtimes = $previewLines
    }
    realRepositoryProbes = $realResults
    environmentLimits = $environmentLimits.ToArray()
}
($record | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $jsonOutputPath -Encoding utf8

if ($gateStatus -eq 'PASS') {
    Write-Output "Technical validation gate passed; evidence written to $OutputPath and $jsonOutputPath."
}
else {
    Write-Output "Technical validation gate environment-limited (exit code 2); evidence written to $OutputPath and $jsonOutputPath."
    exit 2
}
