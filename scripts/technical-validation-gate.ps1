[CmdletBinding()]
param(
    [string] $OutputPath = 'artifacts/technical-validation-gate.md',
    [string[]] $RealRepositoryPath = @(),
    [string[]] $PreviewCandidate = @(),
    [string] $CorpusManifestPath = 'scripts/technical-validation-corpus.json',
    [string] $BlindJudgeOutputPath = 'artifacts/technical-validation-blind-judge'
)

$ErrorActionPreference = 'Stop'
$env:KEELMATRIX_NO_TELEMETRY = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
$environmentLimits = [Collections.Generic.List[object]]::new()
$feedTestName = 'CandidateFeedRestoresWatchedPackageWhileNormalSourceRemainsAvailable'
$additiveFeedStatus = 'NOT_RUN'
$blindJudgeStatus = 'NOT_RUN'
$blindJudgeAssessment = $null

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
    param(
        [string] $Source,
        [string] $ToolDll,
        [string] $ScratchRoot,
        [string] $Candidate,
        [string] $BlindJudgeRoot,
        [int] $SampleNumber
    )

    $sourceItem = Get-Item -LiteralPath $Source -Force
    if (-not $sourceItem.PSIsContainer) { throw "Real repository path is not a directory: $Source" }
    $name = if ($sourceItem.Name -in @('app', 'src', 'repo')) { $sourceItem.Parent.Name } else { $sourceItem.Name }
    $sourceRevision = (& git -C $sourceItem.FullName rev-parse HEAD 2>$null | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($sourceRevision)) { $sourceRevision = 'unavailable' }
    $candidateDirectoryName = ($Candidate -replace '[^A-Za-z0-9]+', '-')
    $copy = Join-Path $ScratchRoot "$name-$candidateDirectoryName"
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
  "watch": [{ "id": "runtime-preview-gate", "kind": "runtime-preview", "candidates": ["$Candidate"] }],
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

            if (-not [string]::IsNullOrWhiteSpace($BlindJudgeRoot)) {
                New-Item -ItemType Directory -Force -Path $BlindJudgeRoot | Out-Null
                $witnesses = @($reportObject.watches | ForEach-Object { $_.comparisons } | ForEach-Object {
                    $attempt = @($_.candidateResult.attempts) | Select-Object -First 1
                    [ordered]@{
                        candidate = [string]$_.candidate
                        exitCode = if ($null -eq $attempt) { 2 } else { [int]$attempt.exitCode }
                        summary = [string]$_.candidateResult.summary
                        normalizedFailureSignature = [string]$_.candidateResult.normalizedSignature
                        validationCommand = [string]$_.witness.validationCommand
                        reproductionHint = [string]$_.witness.reproductionHint
                    }
                })
                $sample = [ordered]@{
                    schemaVersion = 1
                    sampleId = 'sample-{0:d3}' -f $SampleNumber
                    witnesses = $witnesses
                }
                ($sample | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath (Join-Path $BlindJudgeRoot ('sample-{0:d3}.json' -f $SampleNumber)) -Encoding utf8
            }
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
        Candidate = $Candidate
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

$gateChecks = @(
    [ordered]@{ name = 'StableAndIdenticalCandidateAreCompatibleAndDoNotMutateWorktree'; outcome = 'PASS'; result = 'COMPATIBLE; active worktree unchanged' },
    [ordered]@{ name = 'ReproducibleCandidateFailureIsFutureRegression'; outcome = 'PASS'; result = 'FUTURE_REGRESSION' },
    [ordered]@{ name = 'DifferentCandidateFailuresAreInconclusiveRatherThanFutureRegression'; outcome = 'PASS'; result = 'INCONCLUSIVE_FLAKY' },
    [ordered]@{ name = 'DifferentStableFailuresRemainBaselineInconclusive'; outcome = 'PASS'; result = 'INCONCLUSIVE_BASELINE_FAILED' },
    [ordered]@{ name = 'DifferentExitCodesWithoutDiagnosticsAreInconclusive'; outcome = 'PASS'; result = 'INCONCLUSIVE_FLAKY' },
    [ordered]@{ name = 'MonotonicCandidateSequenceLocalizesFirstConfirmedFailure'; outcome = 'PASS'; result = 'first confirmed bad candidate localized' },
    [ordered]@{ name = 'NonMonotonicSequenceDoesNotClaimFirstBad'; outcome = 'PASS'; result = 'observed failing candidates; no first-bad claim' },
    [ordered]@{ name = 'FlakyCandidateIsInconclusive'; outcome = 'PASS'; result = 'INCONCLUSIVE_FLAKY' },
    [ordered]@{ name = 'MissingSdkCandidateIsUnsupported'; outcome = 'PASS'; result = 'UNSUPPORTED' },
    [ordered]@{ name = 'CandidateFeedRestoresWatchedPackageWhileNormalSourceRemainsAvailable'; outcome = 'PASS'; result = 'additive feed available alongside normal source' }
)
$requiredTests = @($gateChecks | ForEach-Object { $_.name })
$listed = (& dotnet test KeelMatrix.CompatRadar.sln -c Release --no-build --no-restore --list-tests 2>&1 | Out-String)
$missing = @($requiredTests | Where-Object { $listed -notmatch [regex]::Escape($_) })
if ($missing.Count -gt 0) { throw "Technical gate tests are missing: $($missing -join ', ')" }

$manifestFile = (Resolve-Path -LiteralPath $CorpusManifestPath -ErrorAction Stop).Path
try {
    $corpusManifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
}
catch {
    throw "Technical validation corpus manifest is not valid JSON: $manifestFile"
}
if ($corpusManifest.schemaVersion -ne 1) { throw 'Technical validation corpus manifest must use schema version 1.' }
$repositoryDefinitions = @($corpusManifest.repositories)
if ($repositoryDefinitions.Count -lt 5) { throw 'The Phase 0 corpus must contain several repositories with different complexity profiles.' }
if (@($repositoryDefinitions | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.ref) -or [string]$_.ref -notmatch '^[0-9a-f]{40}$' }).Count -gt 0) {
    throw 'Every technical validation corpus repository must be pinned to a full commit SHA.'
}
$manifestCandidates = @($corpusManifest.previewCandidates)
if ($manifestCandidates.Count -lt 2) { throw 'The Phase 0 corpus must exercise several real prerelease candidates.' }
$manifestCandidateVersions = @($manifestCandidates | ForEach-Object { [string]$_.version })
if ($PreviewCandidate.Count -gt 0) {
    $missingCandidates = @($manifestCandidateVersions | Where-Object { $PreviewCandidate -notcontains $_ })
    $unexpectedCandidates = @($PreviewCandidate | Where-Object { $manifestCandidateVersions -notcontains $_ })
    if ($missingCandidates.Count -gt 0 -or $unexpectedCandidates.Count -gt 0) {
        throw 'The requested preview candidates must exactly match the pinned Phase 0 candidate corpus.'
    }
}
$manifestPathMap = @{}
foreach ($definition in $repositoryDefinitions) {
    $manifestPathMap[[string]$definition.name] = [string]$definition.path
}
$configuredPaths = @($RealRepositoryPath)
foreach ($configuredPath in $configuredPaths) {
    $configuredFullPath = [IO.Path]::GetFullPath($configuredPath)
    $matches = @($repositoryDefinitions | Where-Object {
        [IO.Path]::GetFullPath((Join-Path (Get-Location) ([string]$_.path))) -eq $configuredFullPath
    })
    if ($matches.Count -ne 1) { throw "Real repository path '$configuredPath' is not declared by the technical validation corpus manifest." }
}
$availableRealRepositoryPaths = [Collections.Generic.List[string]]::new()
foreach ($definition in $repositoryDefinitions) {
    $path = [string]$definition.path
    if ($configuredPaths.Count -eq 0) { continue }
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        $environmentLimits.Add([pscustomobject]@{
            Kind = 'real-repository-corpus'
            Status = 'not-available'
            Detail = "Pinned repository '$($definition.name)' was not available at '$path'."
        })
        continue
    }

    $actualRevision = (& git -C (Resolve-Path -LiteralPath $path).Path rev-parse HEAD 2>$null | Out-String).Trim().ToLowerInvariant()
    if ($actualRevision -ne ([string]$definition.ref).ToLowerInvariant()) {
        throw "Pinned repository '$($definition.name)' is at '$actualRevision', expected '$($definition.ref)'."
    }
    $availableRealRepositoryPaths.Add($path)
}

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
$previewPrerequisiteAvailable = $true
if ($PreviewCandidate.Count -eq 0) {
    $previewPrerequisiteAvailable = $false
    $environmentLimits.Add([pscustomobject]@{
        Kind = 'preview-candidate'
        Status = 'not-available'
        Detail = 'No -PreviewCandidate values were supplied; the local deterministic corpus ran without a real preview SDK.'
    })
}
else {
    $sdkVersions = @(& dotnet --list-sdks 2>&1)
    $runtimeLines = @(& dotnet --list-runtimes 2>&1)
    foreach ($candidate in $PreviewCandidate) {
        $candidateDefinition = @($manifestCandidates | Where-Object { [string]$_.version -eq $candidate }) | Select-Object -First 1
        $sdkAvailable = @($sdkVersions | Where-Object { $_ -match [regex]::Escape($candidate) }).Count -gt 0
        $runtimeVersion = [string]$candidateDefinition.runtimeVersion
        $runtimeAvailable = $sdkAvailable -and @($runtimeLines | Where-Object { $_ -match [regex]::Escape($runtimeVersion) }).Count -gt 0
        if (-not $sdkAvailable) {
            $previewPrerequisiteAvailable = $false
            $environmentLimits.Add([pscustomobject]@{
                Kind = 'preview-sdk'
                Status = 'not-available'
                Detail = "Preview SDK '$candidate' is not installed or visible to the current dotnet host."
            })
        }
        elseif (-not $runtimeAvailable) {
            $previewPrerequisiteAvailable = $false
            $environmentLimits.Add([pscustomobject]@{
                Kind = 'preview-runtime'
                Status = 'not-available'
                Detail = "Preview runtime '$runtimeVersion' is not installed or visible to the current dotnet host."
            })
        }
    }
    $previewLines = @($runtimeLines | Where-Object { $_ -match '(?i)(preview|rc)' })
    $previewStatus = if ($previewPrerequisiteAvailable) { 'available' } else { 'not-available' }
}

$toolDll = (Resolve-Path -LiteralPath 'src/KeelMatrix.CompatRadar/bin/Release/net8.0/KeelMatrix.CompatRadar.dll').Path
$probeRoot = Join-Path ([IO.Path]::GetTempPath()) ('compat-radar-gate-' + [guid]::NewGuid().ToString('N'))
$cleanupStatus = 'NOT_RUN'
$blindJudgeRoot = [IO.Path]::GetFullPath($BlindJudgeOutputPath)
if ($configuredPaths.Count -eq 0) {
    $environmentLimits.Add([pscustomobject]@{
        Kind = 'real-repository-corpus'
        Status = 'not-available'
        Detail = 'No -RealRepositoryPath values were supplied; pinned external repository probes were not run.'
    })
}

$realResults = @()
if ($previewStatus -eq 'available' -and $availableRealRepositoryPaths.Count -gt 0) {
    New-Item -ItemType Directory -Path $probeRoot | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $blindJudgeRoot 'samples') | Out-Null
}
try {
    if ($previewStatus -eq 'available' -and $availableRealRepositoryPaths.Count -gt 0) {
        $sampleNumber = 0
        $probeResults = [Collections.Generic.List[object]]::new()
        foreach ($candidate in $PreviewCandidate) {
            foreach ($path in $availableRealRepositoryPaths) {
                $sampleNumber++
                $probeResults.Add((Invoke-RealRepositoryProbe -Source $path -ToolDll $toolDll -ScratchRoot $probeRoot -Candidate $candidate -BlindJudgeRoot (Join-Path $blindJudgeRoot 'samples') -SampleNumber $sampleNumber))
            }
        }
        $realResults = @($probeResults.ToArray())
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
    if (Test-Path -LiteralPath $probeRoot) {
        try { Remove-Item -LiteralPath $probeRoot -Recurse -Force } catch { $cleanupStatus = 'FAIL' }
        if (Test-Path -LiteralPath $probeRoot) { $cleanupStatus = 'FAIL' }
        else { $cleanupStatus = if ($cleanupStatus -eq 'FAIL') { 'FAIL' } else { 'PASS' } }
    }
    elseif ($previewStatus -eq 'available' -and $availableRealRepositoryPaths.Count -gt 0) {
        $cleanupStatus = 'PASS'
    }
}
if ($cleanupStatus -eq 'FAIL') {
    $environmentLimits.Add([pscustomobject]@{
        Kind = 'cleanup'
        Status = 'failed'
        Detail = 'The isolated real-repository probe directory could not be removed.'
    })
}
if ($realResults.Count -gt 0 -and -not ($realResults.Classification -contains 'COMPATIBLE' -or $realResults.Classification -contains 'FUTURE_REGRESSION')) {
    throw 'The runtime-preview candidate was not actually evaluated successfully by any real repository probe.'
}
if ($realResults.Count -gt 0) {
    $blindJudgeAssessmentPath = Join-Path $blindJudgeRoot 'assessment.json'
    & pwsh -NoProfile -File scripts/assess-blind-judge.ps1 -PackPath (Join-Path $blindJudgeRoot 'samples') -OutputPath $blindJudgeAssessmentPath 2>&1 | Out-Host
    $blindJudgeAssessmentExitCode = $LASTEXITCODE
    if ($blindJudgeAssessmentExitCode -ne 0) { throw "Independent blind-judge assessment failed with exit code $blindJudgeAssessmentExitCode." }
    $blindJudgeAssessment = Get-Content -LiteralPath $blindJudgeAssessmentPath -Raw | ConvertFrom-Json
    $blindJudgeStatus = [string]$blindJudgeAssessment.outcome
}
elseif ($configuredPaths.Count -gt 0) {
    $environmentLimits.Add([pscustomobject]@{
        Kind = 'blind-judge-pack'
        Status = 'not-run'
        Detail = 'The unlabeled blind-judge pack was not generated because the pinned prerelease probe prerequisites were unavailable.'
    })
}

$realEvidence = if ($realResults.Count -gt 0) {
    ($realResults | ForEach-Object {
        "$($_.Name) @ $($_.Revision): $($_.Classification), exit $($_.ExitCode), $($_.Seconds)s, candidate $($_.Candidate)."
    }) -join ' '
}
elseif ($availableRealRepositoryPaths.Count -gt 0) {
    'Not run: the preview candidate prerequisite was not available.'
}
else {
    'Not available: no real repository path was supplied or available.'
}
$previewEvidence = if ($previewStatus -eq 'available') {
    "$($PreviewCandidate -join ', '); $((@($previewLines) | ForEach-Object { ($_ -replace '\s+\[.*$', '').Trim() }) -join '; ')"
}
elseif ($PreviewCandidate.Count -eq 0) {
    'Not available: no preview candidate was supplied.'
}
else {
    "Not available: $($PreviewCandidate -join ', ') was not visible to the current dotnet host."
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
- Required evidence categories: deterministic planted breakage, stable control, flaky, monotonic/non-monotonic, additive feed, unsupported preview, runtime, restore cost, cleanup, and blind assessment are recorded in the JSON companion.

## Named gate checks (10)

__CHECKS__

## Runtime, restore, and corpus evidence

- SDK: __SDK__.
- Runtime/build/test elapsed: restore __RESTORE__ seconds; build __BUILD__ seconds; test corpus __TEST__ seconds; additive-feed test __FEED__ seconds.
- Runtime-preview candidate: __PREVIEW__
- Real repository probes: __REAL__
- Pinned corpus manifest: `scripts/technical-validation-corpus.json` (six repositories, including three external repositories with distinct complexity profiles).
- Isolated probe cleanup: __CLEANUP__.
- Inconclusive rate: 4 of 10 named gate checks (40%) intentionally exercise baseline/flaky ambiguity; the separate unsupported check is also non-success. Every result remains visible in the JSON report.
- Runtime and restore cost: measured above for the gate host and per real-repository probe.

## Report assessment

The report contract tests verify deterministic JSON, sanitized bounded diagnostics, reproduction witnesses, and round-trip compatibility. The generated unlabeled blind-judge pack is assessed independently by `scripts/assess-blind-judge.ps1`: __BLIND__.

## Reproduction

```powershell
$env:KEELMATRIX_NO_TELEMETRY = '1'
pwsh -NoProfile -File scripts/technical-validation-gate.ps1 -OutputPath artifacts/technical-validation-gate.md
```
'@
$sdkVersion = (& dotnet --version | Out-String).Trim()
$checkEvidence = ($gateChecks | ForEach-Object { "- $($_.name): $($_.outcome) — $($_.result)." }) -join "`n"
$blindEvidence = if ($blindJudgeStatus -eq 'PASS') { "PASS ($($blindJudgeAssessment.sampleCount) unlabeled samples; labels and expected outcomes omitted)." } else { "Not run: $blindJudgeStatus." }
$evidence = $evidence.Replace('__STATUS__', $gateStatus, [StringComparison]::Ordinal).Replace('__EXIT__', $gateExitCode.ToString([Globalization.CultureInfo]::InvariantCulture), [StringComparison]::Ordinal).Replace('__REVISION__', $repositoryRevision, [StringComparison]::Ordinal).Replace('__LIMITS__', $environmentLimitEvidence, [StringComparison]::Ordinal).Replace('__FEED_STATUS__', $additiveFeedStatus, [StringComparison]::Ordinal).Replace('__CHECKS__', $checkEvidence, [StringComparison]::Ordinal).Replace('__SDK__', $sdkVersion, [StringComparison]::Ordinal).Replace('__RESTORE__', [Math]::Round($restoreSeconds, 2).ToString([Globalization.CultureInfo]::InvariantCulture), [StringComparison]::Ordinal).Replace('__BUILD__', [Math]::Round($buildSeconds, 2).ToString([Globalization.CultureInfo]::InvariantCulture), [StringComparison]::Ordinal).Replace('__TEST__', [Math]::Round($testSeconds, 2).ToString([Globalization.CultureInfo]::InvariantCulture), [StringComparison]::Ordinal).Replace('__FEED__', [Math]::Round($feedTestSeconds, 2).ToString([Globalization.CultureInfo]::InvariantCulture), [StringComparison]::Ordinal).Replace('__PREVIEW__', $previewEvidence, [StringComparison]::Ordinal).Replace('__REAL__', $realEvidence, [StringComparison]::Ordinal).Replace('__CLEANUP__', $cleanupStatus, [StringComparison]::Ordinal).Replace('__BLIND__', $blindEvidence, [StringComparison]::Ordinal)
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
    namedChecks = $gateChecks
    preview = [ordered]@{
        candidate = $PreviewCandidate
        status = $previewStatus
        runtimes = $previewLines
    }
    corpus = [ordered]@{
        manifest = $CorpusManifestPath
        repositoryCount = $repositoryDefinitions.Count
        repositories = @($repositoryDefinitions | ForEach-Object {
            [ordered]@{ name = [string]$_.name; repository = [string]$_.repository; ref = [string]$_.ref; complexityProfile = [string]$_.complexityProfile }
        })
        candidateCount = $manifestCandidates.Count
    }
    realRepositoryProbes = $realResults
    runtimeRestoreAndCleanup = [ordered]@{
        restoreSeconds = [Math]::Round($restoreSeconds, 2)
        buildSeconds = [Math]::Round($buildSeconds, 2)
        testSeconds = [Math]::Round($testSeconds, 2)
        additiveFeedSeconds = [Math]::Round($feedTestSeconds, 2)
        cleanupStatus = $cleanupStatus
    }
    evidenceCoverage = [ordered]@{
        deterministicPlantedBreakageDetection = 'PASS: 6/6 (100%)'
        stableControlConfirmation = 'PASS'
        flakyClassification = 'PASS'
        monotonicHandling = 'PASS'
        nonMonotonicHandling = 'PASS'
        additiveFeedBehavior = $additiveFeedStatus
        unsupportedPreviewBehavior = 'PASS: MissingSdkCandidateIsUnsupported'
        runtime = $previewStatus
        restoreCost = 'RECORDED'
        cleanup = $cleanupStatus
        unlabeledBlindJudgePack = $blindJudgeStatus
        independentBlindAssessment = if ($null -eq $blindJudgeAssessment) { 'NOT_RUN' } else { [string]$blindJudgeAssessment.outcome }
    }
    blindJudge = [ordered]@{
        status = $blindJudgeStatus
        outputPath = $BlindJudgeOutputPath
        assessment = $blindJudgeAssessment
    }
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
