[CmdletBinding()]
param(
    [string] $OutputPath = 'artifacts/validation-corpus.md',
    [string[]] $RealRepositoryPath = @(),
    [string[]] $PreviewCandidate = @(),
    [string] $CorpusManifestPath = 'scripts/validation-corpus.json',
    [string] $WitnessPackOutputPath = 'artifacts/validation-witness-pack'
)

# Produces reproducible evidence for the pinned validation corpus: it restores and builds the
# solution, runs the deterministic regression corpus, recomputes detection metrics from the
# classifications those tests actually observed, probes the pinned repositories with real
# prerelease candidates when the environment provides them, and writes an unlabeled witness pack
# for an independent reviewer. The script never asserts an independent review outcome.

$ErrorActionPreference = 'Stop'
$env:KEELMATRIX_NO_TELEMETRY = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
$environmentLimits = [Collections.Generic.List[object]]::new()
$feedTestName = 'CandidateFeedRestoresWatchedPackageWhileNormalSourceRemainsAvailable'
$additiveFeedStatus = 'NOT_RUN'
$witnessPackStatus = 'NOT_RUN'
$witnessPackValidation = $null
$observedOutcomes = @{}
$observedOutcomeLines = 0
$deterministicTestFailure = $false
$deterministicFailureEvidence = ''
$testResultsDirectory = Join-Path ([IO.Path]::GetTempPath()) ('compat-radar-test-results-' + [guid]::NewGuid().ToString('N'))
$observedOutcomesPath = Join-Path ([IO.Path]::GetTempPath()) ('compat-radar-observed-outcomes-' + [guid]::NewGuid().ToString('N') + '.jsonl')

function Test-ExternalFeedUnavailable {
    param([string] $Output)

    return $Output -match '(?i)(NU1301|NU1302|unable to load the service index|unable to get repository signature information|timed out|name resolution|connection refused|network is unreachable|could not resolve host)'
}

function Convert-RunAttempts {
    param([object[]] $Attempts)

    $converted = [Collections.Generic.List[object]]::new()
    $number = 0
    foreach ($attempt in @($Attempts)) {
        $number++
        $converted.Add([ordered]@{
            attempt = $number
            exitCode = [int]$attempt.exitCode
            timedOut = [bool]$attempt.timedOut
            cancelled = [bool]$attempt.cancelled
            terminationRequested = [bool]$attempt.terminationRequested
            failureKind = [string]$attempt.failureKind
            summary = if ([string]::IsNullOrWhiteSpace([string]$attempt.summary)) { 'no diagnostic observed' } else { [string]$attempt.summary }
            normalizedFailureSignature = if ([string]::IsNullOrWhiteSpace([string]$attempt.normalizedFailureSignature)) { 'no-failure-observed' } else { [string]$attempt.normalizedFailureSignature }
            fingerprint = if ([string]::IsNullOrWhiteSpace([string]$attempt.fingerprint)) { 'no-failure-observed' } else { [string]$attempt.fingerprint }
        })
    }
    return $converted.ToArray()
}

function Read-TestOutcomes {
    param([string] $ResultsDirectory)

    $outcomes = @{}
    foreach ($trx in @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*.trx' -File -Recurse -ErrorAction SilentlyContinue)) {
        foreach ($node in @(Select-Xml -LiteralPath $trx.FullName -XPath "//*[local-name()='UnitTestResult']")) {
            $testName = [string]$node.Node.GetAttribute('testName')
            $outcome = [string]$node.Node.GetAttribute('outcome')
            if (-not [string]::IsNullOrWhiteSpace($testName)) { $outcomes[$testName] = $outcome }
        }
    }
    return $outcomes
}

function Read-ObservedOutcomes {
    param([string] $Path)

    $observed = @{}
    if (-not (Test-Path -LiteralPath $Path)) { return $observed }
    foreach ($line in @(Get-Content -LiteralPath $Path -ErrorAction SilentlyContinue)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try {
            $record = $line | ConvertFrom-Json
            $caseId = [string]$record.case
            if ([string]::IsNullOrWhiteSpace($caseId)) { continue }
            if (-not $observed.ContainsKey($caseId)) { $observed[$caseId] = [Collections.Generic.List[string]]::new() }
            $observed[$caseId].Add([string]$record.classification)
        }
        catch {
            throw "Observed-outcome record is not valid JSON: $line"
        }
    }
    return $observed
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
        [string] $SdkCandidate,
        [string] $RuntimeCandidate,
        [string] $WitnessPackRoot,
        [int] $SampleNumber
    )

    $sourceItem = Get-Item -LiteralPath $Source -Force
    if (-not $sourceItem.PSIsContainer) { throw "Real repository path is not a directory: $Source" }
    $name = if ($sourceItem.Name -in @('app', 'src', 'repo')) { $sourceItem.Parent.Name } else { $sourceItem.Name }
    $sourceRevision = (& git -C $sourceItem.FullName rev-parse HEAD 2>$null | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($sourceRevision)) { $sourceRevision = 'unavailable' }
    $repositoryIdentity = (& git -C $sourceItem.FullName config --get remote.origin.url 2>$null | Out-String).Trim()
    if (-not [string]::IsNullOrWhiteSpace($repositoryIdentity)) {
        try {
            $repositoryUri = [Uri]$repositoryIdentity
            if ($repositoryUri.Scheme -in @('http', 'https', 'ssh')) {
                $repositoryIdentity = "$($repositoryUri.Scheme)://$($repositoryUri.Host)$($repositoryUri.AbsolutePath)".TrimEnd('/')
            }
            elseif ($repositoryIdentity -notmatch '^git@[^:]+:.+') {
                $repositoryIdentity = 'unavailable'
            }
        }
        catch {
            if ($repositoryIdentity -notmatch '^git@[^:]+:.+') { $repositoryIdentity = 'unavailable' }
        }
    }
    if ([string]::IsNullOrWhiteSpace($repositoryIdentity)) { $repositoryIdentity = 'unavailable' }
    $candidateDirectoryName = ($RuntimeCandidate -replace '[^A-Za-z0-9]+', '-')
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
  "watch": [{ "id": "runtime-preview-corpus", "kind": "runtime-preview", "candidates": ["$RuntimeCandidate"] }],
  "validation": { "command": "dotnet test \"$relativeTarget\" --nologo", "workingDirectory": ".", "timeoutSeconds": 900 },
  "policy": { "confirmationRuns": 1 }
}
"@
    Set-Content -LiteralPath $config -Value $configText -Encoding utf8
    $report = Join-Path $copy 'compat-radar-report.json'
    $started = [Diagnostics.Stopwatch]::StartNew()
    Push-Location -LiteralPath $copy
    try {
        & dotnet $ToolDll check --config compat-radar.json --format json --report compat-radar-report.json 2>&1 | Out-Host
        $status = $LASTEXITCODE
    }
    finally { Pop-Location }
    $started.Stop()

    $classification = 'INCONCLUSIVE_EXECUTION'
    if (Test-Path -LiteralPath $report) {
        try {
            $reportObject = Get-Content -Raw -LiteralPath $report | ConvertFrom-Json
            $classification = [string]$reportObject.watches[0].comparisons[0].classification

            if (-not [string]::IsNullOrWhiteSpace($WitnessPackRoot)) {
                New-Item -ItemType Directory -Force -Path $WitnessPackRoot | Out-Null

                # The witness pack intentionally omits classifications, expectations, and every
                # other label so a reviewer can judge attribution and reproduction usefulness
                # without knowing which outcome was seeded for the sample.
                $witnesses = @($reportObject.watches | ForEach-Object { $_.comparisons } | ForEach-Object {
                    $comparison = $_
                    $witness = $comparison.witness
                    $reportIdentity = [string]$witness.repositoryIdentity
                    $reportConfiguration = [string]$witness.reproductionConfiguration
                    if ([string]::IsNullOrWhiteSpace($reportIdentity) -or [string]::IsNullOrWhiteSpace($reportConfiguration)) {
                        throw "The report witness for '$($comparison.candidate)' is missing repository identity or reproduction configuration."
                    }
                    $candidateAttempts = @(Convert-RunAttempts @($witness.candidateAttempts))
                    $stableAttempts = @(Convert-RunAttempts @($witness.stableAttempts))
                    [ordered]@{
                        candidate = [string]$comparison.candidate
                        repositoryIdentity = if ([string]::IsNullOrWhiteSpace($repositoryIdentity) -or $repositoryIdentity -eq 'unavailable') { $reportIdentity } else { $repositoryIdentity }
                        repositoryRevision = if ($sourceRevision -match '^[0-9a-fA-F]{40}$') { $sourceRevision } else { [string]$witness.repositoryRevision }
                        repositoryContentHash = [string]$witness.repositoryContentHash
                        controlConfiguration = [string]$witness.controlConfiguration
                        candidateInputConfiguration = [string]$witness.candidateInputConfiguration
                        sdk = [string]$witness.sdk
                        runtime = [string]$witness.runtime
                        stableAttempts = $stableAttempts
                        candidateAttempts = $candidateAttempts
                        focusedFailingTest = if ([string]::IsNullOrWhiteSpace([string]$witness.focusedFailure)) { 'unavailable' } else { [string]$witness.focusedFailure }
                        reproductionCommand = [string]$witness.validationCommand
                        reproductionConfiguration = $reportConfiguration
                    }
                })
                $sample = [ordered]@{
                    schemaVersion = 1
                    sampleId = 'sample-{0:d3}' -f $SampleNumber
                    witnesses = $witnesses
                }
                ($sample | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath (Join-Path $WitnessPackRoot ('sample-{0:d3}.json' -f $SampleNumber)) -Encoding utf8
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
        SdkCandidate = $SdkCandidate
        RuntimeCandidate = $RuntimeCandidate
    }
}

$restoreOutput = (& dotnet restore KeelMatrix.CompatRadar.sln --configfile NuGet.config 2>&1 | Out-String)
$restoreStatus = $LASTEXITCODE
$restoreSeconds = $stopwatch.Elapsed.TotalSeconds
if ($restoreStatus -ne 0) { throw "Validation corpus restore failed with exit code $restoreStatus.`n$restoreOutput" }

$stopwatch.Restart()
$buildOutput = (& dotnet build KeelMatrix.CompatRadar.sln -c Release --no-restore -warnaserror 2>&1 | Out-String)
$buildStatus = $LASTEXITCODE
$buildSeconds = $stopwatch.Elapsed.TotalSeconds
if ($buildStatus -ne 0) { throw "Validation corpus Release build failed with exit code $buildStatus.`n$buildOutput" }

$namedChecks = @(
    [ordered]@{ name = 'StableAndIdenticalCandidateAreCompatibleAndDoNotMutateWorktree'; outcome = 'PASS'; result = 'COMPATIBLE; active worktree unchanged' },
    [ordered]@{ name = 'ReproducibleCandidateFailureIsFutureRegression'; outcome = 'PASS'; result = 'FUTURE_REGRESSION from a removed dependency API' },
    [ordered]@{ name = 'DifferentCandidateFailuresAreInconclusiveRatherThanFutureRegression'; outcome = 'PASS'; result = 'INCONCLUSIVE_FLAKY' },
    [ordered]@{ name = 'DifferentStableFailuresRemainBaselineInconclusive'; outcome = 'PASS'; result = 'INCONCLUSIVE_BASELINE_FAILED' },
    [ordered]@{ name = 'DifferentExitCodesWithoutDiagnosticsAreInconclusive'; outcome = 'PASS'; result = 'INCONCLUSIVE_FLAKY' },
    [ordered]@{ name = 'MonotonicCandidateSequenceLocalizesFirstConfirmedFailure'; outcome = 'PASS'; result = 'first confirmed bad candidate localized' },
    [ordered]@{ name = 'NonMonotonicSequenceDoesNotClaimFirstBad'; outcome = 'PASS'; result = 'observed failing candidates; no first-bad claim' },
    [ordered]@{ name = 'FlakyCandidateIsInconclusive'; outcome = 'PASS'; result = 'INCONCLUSIVE_FLAKY' },
    [ordered]@{ name = 'MissingRuntimeCandidateIsUnsupported'; outcome = 'PASS'; result = 'UNSUPPORTED' },
    [ordered]@{ name = 'InstalledRuntimePreviewIsSelectedIndependentlyOfTheSdk'; outcome = 'PASS'; result = 'COMPATIBLE; exact runtime selected independently of the SDK' },
    [ordered]@{ name = 'InstalledRuntimePreviewCanProduceAConfirmedFutureRegression'; outcome = 'PASS'; result = 'FUTURE_REGRESSION; runtime contract violated by the candidate runtime' },
    [ordered]@{ name = 'InstalledSdkCandidateCanProduceAConfirmedFutureRegression'; outcome = 'PASS'; result = 'FUTURE_REGRESSION; SDK contract violated by the candidate SDK' },
    [ordered]@{ name = 'WitnessRecordsTheTestedContentIdentityForADirtyWorktree'; outcome = 'PASS'; result = 'witness records the materialized content identity' },
    [ordered]@{ name = 'ComparisonEnvironmentDoesNotDescribeTheWatchedCandidate'; outcome = 'PASS'; result = 'comparison environment carries no candidate signaling' },
    [ordered]@{ name = 'CandidateFeedRestoresWatchedPackageWhileNormalSourceRemainsAvailable'; outcome = 'PASS'; result = 'additive feed available alongside normal source' }
)
$requiredTests = @($namedChecks | ForEach-Object { $_.name })
$listed = (& dotnet test KeelMatrix.CompatRadar.sln -c Release --no-build --no-restore --list-tests 2>&1 | Out-String)
$missing = @($requiredTests | Where-Object { $listed -notmatch [regex]::Escape($_) })
if ($missing.Count -gt 0) { throw "Validation corpus tests are missing: $($missing -join ', ')" }

$manifestFile = (Resolve-Path -LiteralPath $CorpusManifestPath -ErrorAction Stop).Path
try {
    $corpusManifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
}
catch {
    throw "Validation corpus manifest is not valid JSON: $manifestFile"
}
if ($corpusManifest.schemaVersion -ne 1) { throw 'Validation corpus manifest must use schema version 1.' }
$repositoryDefinitions = @($corpusManifest.repositories)
if ($repositoryDefinitions.Count -lt 5) { throw 'The validation corpus must contain several repositories with different complexity profiles.' }
if (@($repositoryDefinitions | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.ref) -or [string]$_.ref -notmatch '^[0-9a-f]{40}$' }).Count -gt 0) {
    throw 'Every validation corpus repository must be pinned to a full commit SHA.'
}
$manifestCandidates = @($corpusManifest.previewCandidates)
if ($manifestCandidates.Count -lt 2) { throw 'The validation corpus must exercise several real prerelease candidates.' }
$manifestCandidateVersions = @($manifestCandidates | ForEach-Object { [string]$_.version })
if ($PreviewCandidate.Count -gt 0) {
    $missingCandidates = @($manifestCandidateVersions | Where-Object { $PreviewCandidate -notcontains $_ })
    $unexpectedCandidates = @($PreviewCandidate | Where-Object { $manifestCandidateVersions -notcontains $_ })
    if ($missingCandidates.Count -gt 0 -or $unexpectedCandidates.Count -gt 0) {
        throw 'The requested preview candidates must exactly match the pinned candidate corpus.'
    }
}
$expectedOutcomeDefinitions = @($corpusManifest.expectedOutcomes)
if ($expectedOutcomeDefinitions.Count -eq 0) { throw 'The validation corpus manifest must declare the expected outcome of every recorded case.' }
if (@($expectedOutcomeDefinitions | Where-Object {
        [string]::IsNullOrWhiteSpace([string]$_.case) -or
        [string]$_.expectedClassification -notin @('COMPATIBLE', 'FUTURE_REGRESSION') -or
        [string]$_.group -notin @('equivalent', 'incompatible')
    }).Count -gt 0) {
    throw 'Every expected outcome must name a case, an observed classification, and an equivalent or incompatible group.'
}
if (@($expectedOutcomeDefinitions | Where-Object { [string]$_.group -eq 'equivalent' }).Count -eq 0) {
    throw 'The validation corpus must include equivalent-state cases that must never report a future regression.'
}
if (@($expectedOutcomeDefinitions | Where-Object { [string]$_.group -eq 'incompatible' }).Count -eq 0) {
    throw 'The validation corpus must include cases with a genuinely incompatible candidate state.'
}

$configuredPaths = @($RealRepositoryPath)
foreach ($configuredPath in $configuredPaths) {
    $configuredFullPath = [IO.Path]::GetFullPath($configuredPath)
    $matches = @($repositoryDefinitions | Where-Object {
        [IO.Path]::GetFullPath((Join-Path (Get-Location) ([string]$_.path))) -eq $configuredFullPath
    })
    if ($matches.Count -ne 1) { throw "Real repository path '$configuredPath' is not declared by the validation corpus manifest." }
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
New-Item -ItemType Directory -Force -Path $testResultsDirectory | Out-Null
$env:COMPATRADAR_TEST_OUTCOMES_PATH = $observedOutcomesPath
try {
    $testOutput = (& dotnet test KeelMatrix.CompatRadar.sln -c Release --no-build --no-restore --logger 'trx;LogFileName=validation-corpus.trx' --results-directory $testResultsDirectory 2>&1 | Out-String)
    $testStatus = $LASTEXITCODE
}
finally {
    Remove-Item Env:COMPATRADAR_TEST_OUTCOMES_PATH -ErrorAction SilentlyContinue
}
$testSeconds = $stopwatch.Elapsed.TotalSeconds
if ($testStatus -ne 0) {
    if (Test-ExternalFeedUnavailable $testOutput) {
        $additiveFeedStatus = 'ENVIRONMENT_LIMITED'
        $environmentLimits.Add([pscustomobject]@{
            Kind = 'additive-feed-integration'
            Status = 'not-available'
            Detail = 'The named additive-feed integration test could not reach its external NuGet source; the remaining test results are retained and this run is not a pass.'
        })
    }
    else {
        $deterministicTestFailure = $true
        $deterministicFailureEvidence = "The deterministic regression corpus failed with exit code $testStatus."
    }
}

$testOutcomes = Read-TestOutcomes -ResultsDirectory $testResultsDirectory
$observedOutcomes = Read-ObservedOutcomes -Path $observedOutcomesPath
$observedOutcomeLines = @($observedOutcomes.Values | ForEach-Object { $_ }).Count
if (Test-Path -LiteralPath $testResultsDirectory) {
    try { Remove-Item -LiteralPath $testResultsDirectory -Recurse -Force } catch { $environmentLimits.Add([pscustomobject]@{ Kind = 'test-results-cleanup'; Status = 'failed'; Detail = 'The temporary test-result directory could not be removed.' }) }
}
if (Test-Path -LiteralPath $observedOutcomesPath) { Remove-Item -LiteralPath $observedOutcomesPath -Force -ErrorAction SilentlyContinue }

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
            Detail = 'The named additive-feed integration test could not reach its external NuGet source; this run is not a pass.'
        })
    }
    $additiveFeedStatus = 'ENVIRONMENT_LIMITED'
}
else {
    throw "Validation corpus additive-feed integration test failed with exit code $feedTestStatus.`n$feedTestOutput"
}

foreach ($check in $namedChecks) {
    $checkName = [string]$check.name
    if ($checkName -eq $feedTestName) {
        $check['outcome'] = if ($additiveFeedStatus -eq 'PASS') { 'PASS' } else { 'NOT_RUN' }
        $check['result'] = if ($additiveFeedStatus -eq 'PASS') { 'additive feed available alongside normal source' } else { "additive-feed status: $additiveFeedStatus" }
        continue
    }
    $matchingTest = @($testOutcomes.Keys | Where-Object { $_ -eq $checkName -or $_ -like "*.$checkName" }) | Select-Object -First 1
    if ($null -eq $matchingTest) {
        $check['outcome'] = 'FAIL'
        $check['result'] = 'test outcome: NOT_RUN'
        $deterministicTestFailure = $true
    }
    elseif ([string]$testOutcomes[$matchingTest] -ne 'Passed') {
        $check['outcome'] = 'FAIL'
        $check['result'] = "test outcome: $([string]$testOutcomes[$matchingTest])"
    }
}

$observedOutcomeRecords = [Collections.Generic.List[object]]::new()
$incompatibleDetected = 0
$incompatibleTotal = 0
$equivalentFalseRegressions = 0
foreach ($definition in $expectedOutcomeDefinitions) {
    $caseId = [string]$definition.case
    $expected = [string]$definition.expectedClassification
    $group = [string]$definition.group
    $observed = if ($observedOutcomes.ContainsKey($caseId)) { @($observedOutcomes[$caseId]) } else { @() }
    $observedText = if ($observed.Count -eq 0) { 'NOT_RECORDED' } else { ($observed -join ',') }
    $matchesExpectation = $observed.Count -gt 0 -and @($observed | Where-Object { $_ -ne $expected }).Count -eq 0
    if (-not $matchesExpectation) { $deterministicTestFailure = $true }
    if ($group -eq 'incompatible') {
        $incompatibleTotal++
        if ($observed.Count -gt 0 -and @($observed | Where-Object { $_ -eq 'FUTURE_REGRESSION' }).Count -eq $observed.Count) { $incompatibleDetected++ }
    }
    if ($group -eq 'equivalent' -and @($observed | Where-Object { $_ -eq 'FUTURE_REGRESSION' }).Count -gt 0) {
        $equivalentFalseRegressions++
    }
    $observedOutcomeRecords.Add([ordered]@{
        case = $caseId
        group = $group
        expectedClassification = $expected
        observedClassifications = $observed
        matchesExpectation = $matchesExpectation
    })
}
$detectionRatePercent = if ($incompatibleTotal -eq 0) { 0 } else { [Math]::Round(($incompatibleDetected * 100.0) / $incompatibleTotal, 2) }
$requiredDetectionRatePercent = 95
$detectionPasses = -not $deterministicTestFailure -and $detectionRatePercent -ge $requiredDetectionRatePercent -and $equivalentFalseRegressions -eq 0

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
        Detail = 'No -PreviewCandidate values were supplied; the deterministic corpus ran without a real preview SDK.'
    })
}
else {
    $sdkVersions = @(& dotnet --list-sdks 2>&1)
    $runtimeLines = @(& dotnet --list-runtimes 2>&1)
    foreach ($candidate in $PreviewCandidate) {
        $candidateDefinition = @($manifestCandidates | Where-Object { [string]$_.version -eq $candidate }) | Select-Object -First 1
        $sdkAvailable = @($sdkVersions | Where-Object { $_ -match [regex]::Escape($candidate) }).Count -gt 0
        $runtimeVersion = [string]$candidateDefinition.runtimeVersion
        $runtimePattern = '^Microsoft\.NETCore\.App\s+' + [regex]::Escape($runtimeVersion) + '(?:\s|$)'
        $runtimeAvailable = $sdkAvailable -and @($runtimeLines | Where-Object { $_ -match $runtimePattern }).Count -gt 0
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
$probeRoot = Join-Path ([IO.Path]::GetTempPath()) ('compat-radar-corpus-' + [guid]::NewGuid().ToString('N'))
$cleanupStatus = 'NOT_RUN'
$witnessPackRoot = [IO.Path]::GetFullPath($WitnessPackOutputPath)
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
    New-Item -ItemType Directory -Force -Path (Join-Path $witnessPackRoot 'samples') | Out-Null
}
try {
    if ($previewStatus -eq 'available' -and $availableRealRepositoryPaths.Count -gt 0) {
        $sampleNumber = 0
        $probeResults = [Collections.Generic.List[object]]::new()
        foreach ($candidate in $PreviewCandidate) {
            foreach ($path in $availableRealRepositoryPaths) {
                $sampleNumber++
                $candidateDefinition = @($manifestCandidates | Where-Object { [string]$_.version -eq $candidate }) | Select-Object -First 1
                $probeResults.Add((Invoke-RealRepositoryProbe -Source $path -ToolDll $toolDll -ScratchRoot $probeRoot -SdkCandidate $candidate -RuntimeCandidate ([string]$candidateDefinition.runtimeVersion) -WitnessPackRoot (Join-Path $witnessPackRoot 'samples') -SampleNumber $sampleNumber))
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
        Detail = 'The isolated repository probe directory could not be removed.'
    })
}
if ($realResults.Count -gt 0 -and -not ($realResults.Classification -contains 'COMPATIBLE' -or $realResults.Classification -contains 'FUTURE_REGRESSION')) {
    throw 'The runtime-preview candidate was not actually evaluated successfully by any repository probe.'
}
if ($realResults.Count -gt 0) {
    $structurePath = Join-Path $witnessPackRoot 'structure.json'
    & pwsh -NoProfile -File scripts/validate-witness-pack.ps1 -PackPath (Join-Path $witnessPackRoot 'samples') -OutputPath $structurePath 2>&1 | Out-Host
    $structureExitCode = $LASTEXITCODE
    if ($structureExitCode -ne 0) { throw "Witness-pack structural validation failed with exit code $structureExitCode." }
    $witnessPackValidation = Get-Content -LiteralPath $structurePath -Raw | ConvertFrom-Json
    $witnessPackStatus = 'STRUCTURALLY_COMPLETE'
}
elseif ($configuredPaths.Count -gt 0) {
    $environmentLimits.Add([pscustomobject]@{
        Kind = 'witness-pack'
        Status = 'not-run'
        Detail = 'The unlabeled witness pack was not generated because the pinned prerelease probe prerequisites were unavailable.'
    })
}

$realEvidence = if ($realResults.Count -gt 0) {
    ($realResults | ForEach-Object {
        "$($_.Name) @ $($_.Revision): $($_.Classification), exit $($_.ExitCode), $($_.Seconds)s, runtime candidate $($_.RuntimeCandidate)."
    }) -join ' '
}
elseif ($availableRealRepositoryPaths.Count -gt 0) {
    'Not run: the preview candidate prerequisite was not available.'
}
else {
    'Not available: no real repository path was supplied or available.'
}
$previewEvidence = if ($previewStatus -eq 'available') {
    $previewMappings = @($PreviewCandidate | ForEach-Object {
        $sdkCandidate = $_
        $definition = @($manifestCandidates | Where-Object { [string]$_.version -eq $sdkCandidate }) | Select-Object -First 1
        "SDK $sdkCandidate -> runtime $([string]$definition.runtimeVersion)"
    })
    "$($previewMappings -join '; '); installed runtimes: $((@($previewLines) | ForEach-Object { ($_ -replace '\s+\[.*$', '').Trim() }) -join '; ')"
}
elseif ($PreviewCandidate.Count -eq 0) {
    'Not available: no preview candidate was supplied.'
}
else {
    "Not available: $($PreviewCandidate -join ', ') was not visible to the current dotnet host."
}
$repositoryRevision = (& git rev-parse HEAD 2>$null | Out-String).Trim()
if ([string]::IsNullOrWhiteSpace($repositoryRevision)) { $repositoryRevision = 'unavailable' }
$runStatus = if (-not $detectionPasses) { 'FAILED' } elseif ($environmentLimits.Count -eq 0) { 'PASS' } else { 'ENVIRONMENT_LIMITED' }
$runExitCode = if ($runStatus -eq 'PASS') { 0 } elseif ($runStatus -eq 'ENVIRONMENT_LIMITED') { 2 } else { 1 }
$environmentLimitEvidence = if ($environmentLimits.Count -eq 0) {
    'None.'
}
else {
    ($environmentLimits | ForEach-Object { "$($_.Kind): $($_.Status) - $($_.Detail)" }) -join ' '
}
if (-not [string]::IsNullOrWhiteSpace($deterministicFailureEvidence)) {
    $environmentLimitEvidence = "$deterministicFailureEvidence $environmentLimitEvidence"
}
$directory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
$evidence = @'
# CompatRadar validation corpus run

Generated by `scripts/validation-corpus.ps1` with application and CLI telemetry disabled.

## Run status

- Status: __STATUS__ (exit code __EXIT__).
- Repository revision: `__REVISION__`.
- Environment-limited records: __LIMITS__

## Observed compatibility outcomes

- Incompatible candidate states detected: __DETECTED__/__TOTAL__ (__RATE__%, required >=__REQUIRED_RATE__).
- Equivalent stable/future states that reported `FUTURE_REGRESSION`: __EQUIVALENT_REGRESSIONS__.
- Recorded observed classifications: __RECORDED__ from the deterministic regression corpus.
- Incompatible states are produced by dependency versions that remove the API the repository uses, by a candidate SDK the repository does not support, and by a candidate runtime the repository does not support.
- Additive-feed integration: __FEED_STATUS__ (`CandidateFeedRestoresWatchedPackageWhileNormalSourceRemainsAvailable`).
- Stable-control failures: classified `INCONCLUSIVE_BASELINE_FAILED`, including non-deterministic fail-A/fail-B output.
- Candidate fail-A/fail-B output: classified `INCONCLUSIVE_FLAKY`, never `FUTURE_REGRESSION`.
- Flaky and non-monotonic sequences: covered by the named regression tests; no unjustified first-bad claim is emitted.
- Reachable unsupported path: missing SDK/runtime candidate is classified `UNSUPPORTED` with exit code 2.

## Named checks

__CHECKS__

## Runtime, restore, and corpus evidence

- SDK: __SDK__.
- Elapsed time: restore __RESTORE__ seconds; build __BUILD__ seconds; regression corpus __TEST__ seconds; additive-feed test __FEED__ seconds.
- Runtime-preview candidate: __PREVIEW__
- Repository probes: __REAL__
- Pinned corpus manifest: `scripts/validation-corpus.json`.
- Isolated probe cleanup: __CLEANUP__.
- Runtime and restore cost: measured above for this host and per repository probe.

## Reviewer pack

The repository probes write an unlabeled witness pack under `__WITNESS_PACK__`. `scripts/validate-witness-pack.ps1` checks only structural completeness and that labels are absent; it does not judge attribution or reproduction usefulness and it does not assert an independent review outcome: __WITNESS__.

## Reproduction

```powershell
$env:KEELMATRIX_NO_TELEMETRY = '1'
pwsh -NoProfile -File scripts/validation-corpus.ps1 -OutputPath artifacts/validation-corpus.md
```
'@
$sdkVersion = (& dotnet --version | Out-String).Trim()
$checkEvidence = ($namedChecks | ForEach-Object { "- $($_.name): $($_.outcome) - $($_.result)." }) -join "`n"
$witnessEvidence = if ($witnessPackStatus -eq 'STRUCTURALLY_COMPLETE') {
    "structurally complete for $($witnessPackValidation.sampleCount) unlabeled sample(s); outcomes, expectations, and labels are omitted and an independent review is still required."
}
else {
    "Not run: $witnessPackStatus."
}
$replacements = [ordered]@{
    '__STATUS__' = $runStatus
    '__EXIT__' = $runExitCode.ToString([Globalization.CultureInfo]::InvariantCulture)
    '__REVISION__' = $repositoryRevision
    '__LIMITS__' = $environmentLimitEvidence
    '__FEED_STATUS__' = $additiveFeedStatus
    '__CHECKS__' = $checkEvidence
    '__SDK__' = $sdkVersion
    '__RESTORE__' = [Math]::Round($restoreSeconds, 2).ToString([Globalization.CultureInfo]::InvariantCulture)
    '__BUILD__' = [Math]::Round($buildSeconds, 2).ToString([Globalization.CultureInfo]::InvariantCulture)
    '__TEST__' = [Math]::Round($testSeconds, 2).ToString([Globalization.CultureInfo]::InvariantCulture)
    '__FEED__' = [Math]::Round($feedTestSeconds, 2).ToString([Globalization.CultureInfo]::InvariantCulture)
    '__PREVIEW__' = $previewEvidence
    '__REAL__' = $realEvidence
    '__CLEANUP__' = $cleanupStatus
    '__WITNESS__' = $witnessEvidence
    '__WITNESS_PACK__' = $WitnessPackOutputPath
    '__DETECTED__' = $incompatibleDetected.ToString([Globalization.CultureInfo]::InvariantCulture)
    '__TOTAL__' = $incompatibleTotal.ToString([Globalization.CultureInfo]::InvariantCulture)
    '__RATE__' = $detectionRatePercent.ToString([Globalization.CultureInfo]::InvariantCulture)
    '__REQUIRED_RATE__' = $requiredDetectionRatePercent.ToString([Globalization.CultureInfo]::InvariantCulture)
    '__EQUIVALENT_REGRESSIONS__' = $equivalentFalseRegressions.ToString([Globalization.CultureInfo]::InvariantCulture)
    '__RECORDED__' = $observedOutcomeLines.ToString([Globalization.CultureInfo]::InvariantCulture)
}
foreach ($replacement in $replacements.GetEnumerator()) {
    $evidence = $evidence.Replace($replacement.Key, [string]$replacement.Value, [StringComparison]::Ordinal)
}
$evidence | Set-Content -LiteralPath $OutputPath -Encoding utf8

$jsonOutputPath = [IO.Path]::ChangeExtension($OutputPath, '.json')
if ([string]::Equals([IO.Path]::GetFullPath($jsonOutputPath), [IO.Path]::GetFullPath($OutputPath), [StringComparison]::OrdinalIgnoreCase)) {
    $jsonOutputPath = "$OutputPath.summary.json"
}
$jsonDirectory = Split-Path -Parent $jsonOutputPath
if (-not [string]::IsNullOrWhiteSpace($jsonDirectory)) { New-Item -ItemType Directory -Force -Path $jsonDirectory | Out-Null }
$record = [ordered]@{
    schemaVersion = 1
    status = $runStatus
    exitCode = $runExitCode
    repositoryRevision = $repositoryRevision
    observedOutcomes = [ordered]@{
        incompatibleStatesDetected = $incompatibleDetected
        incompatibleStatesTotal = $incompatibleTotal
        detectionRatePercent = $detectionRatePercent
        requiredDetectionRatePercent = $requiredDetectionRatePercent
        equivalentStateFutureRegressionCount = $equivalentFalseRegressions
        detectionPasses = $detectionPasses
        recordedOutcomeCount = $observedOutcomeLines
        cases = $observedOutcomeRecords
    }
    additiveFeedIntegration = [ordered]@{
        test = $feedTestName
        status = $additiveFeedStatus
        seconds = [Math]::Round($feedTestSeconds, 2)
    }
    namedChecks = $namedChecks
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
    repositoryProbes = $realResults
    runtimeRestoreAndCleanup = [ordered]@{
        restoreSeconds = [Math]::Round($restoreSeconds, 2)
        buildSeconds = [Math]::Round($buildSeconds, 2)
        testSeconds = [Math]::Round($testSeconds, 2)
        additiveFeedSeconds = [Math]::Round($feedTestSeconds, 2)
        cleanupStatus = $cleanupStatus
    }
    witnessPack = [ordered]@{
        status = $witnessPackStatus
        outputPath = $WitnessPackOutputPath
        structuralValidation = $witnessPackValidation
        independentReviewRequired = $true
    }
    environmentLimits = $environmentLimits.ToArray()
}
($record | ConvertTo-Json -Depth 12) | Set-Content -LiteralPath $jsonOutputPath -Encoding utf8

if ($runStatus -eq 'PASS') {
    Write-Output "Validation corpus run passed; evidence written to $OutputPath and $jsonOutputPath."
}
elseif ($runStatus -eq 'FAILED') {
    Write-Output "Validation corpus run failed closed; evidence written to $OutputPath and $jsonOutputPath."
    exit $runExitCode
}
else {
    Write-Output "Validation corpus run was environment-limited (exit code 2); evidence written to $OutputPath and $jsonOutputPath."
    exit $runExitCode
}
