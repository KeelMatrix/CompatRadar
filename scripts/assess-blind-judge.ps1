[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackPath,
    [Parameter(Mandatory = $true)]
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
$samples = @(Get-ChildItem -LiteralPath $PackPath -Filter 'sample-*.json' -File | Sort-Object Name)
if ($samples.Count -eq 0) { throw 'Blind-judge pack contains no unlabeled samples.' }

$forbiddenProperties = @('planted', 'expected', 'classification', 'stableControl', 'baseline', 'groundTruth', 'outcome')

function Assert-NonEmpty {
    param([object] $Value, [string] $Field, [string] $SampleName)

    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) {
        throw "Blind-judge sample '$SampleName' has an empty required field '$Field'."
    }
}

function Assert-NoLabels {
    param([object] $Value, [string] $Path, [string] $SampleName)

    if ($null -eq $Value) { return }
    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        foreach ($item in $Value) { Assert-NoLabels -Value $item -Path $Path -SampleName $SampleName }
        return
    }
    if ($Value.PSObject.Properties.Count -eq 0) { return }
    foreach ($property in $Value.PSObject.Properties) {
        if ($forbiddenProperties -contains $property.Name) {
            throw "Blind-judge sample '$SampleName' exposes the '$($property.Name)' label at '$Path.$($property.Name)'."
        }
        Assert-NoLabels -Value $property.Value -Path "$Path.$($property.Name)" -SampleName $SampleName
    }
}

function Assert-Attempts {
    param([object] $Attempts, [string] $Field, [string] $SampleName)

    if ($null -eq $Attempts) { throw "Blind-judge sample '$SampleName' has no '$Field' attempt evidence." }
    $items = @($Attempts)
    if ($items.Count -eq 0) { throw "Blind-judge sample '$SampleName' has empty '$Field' attempt evidence." }
    foreach ($attempt in $items) {
        foreach ($required in @('attempt', 'exitCode', 'summary', 'normalizedFailureSignature', 'fingerprint')) {
            if ($null -eq $attempt.PSObject.Properties[$required]) {
                throw "Blind-judge sample '$SampleName' has '$Field' evidence without '$required'."
            }
            Assert-NonEmpty -Value $attempt.$required -Field "$Field.$required" -SampleName $SampleName
        }
    }
}

foreach ($sample in $samples) {
    $sampleObject = Get-Content -LiteralPath $sample.FullName -Raw | ConvertFrom-Json
    Assert-NoLabels -Value $sampleObject -Path '$' -SampleName $sample.Name
    if ($null -eq $sampleObject.witnesses) { throw "Blind-judge sample '$($sample.Name)' has no witnesses." }
    $witnesses = @($sampleObject.witnesses)
    if ($witnesses.Count -eq 0) { throw "Blind-judge sample '$($sample.Name)' has no witnesses." }
    foreach ($witness in $witnesses) {
        foreach ($required in @(
                'candidate',
                'repositoryIdentity',
                'repositoryRevision',
                'controlConfiguration',
                'candidateInputConfiguration',
                'focusedFailingTest',
                'reproductionCommand',
                'reproductionConfiguration',
                'normalizedFailureSignature')) {
            if ($null -eq $witness.PSObject.Properties[$required]) {
                throw "Blind-judge sample '$($sample.Name)' has a witness without '$required'."
            }
            Assert-NonEmpty -Value $witness.$required -Field $required -SampleName $sample.Name
        }
        if ([string]$witness.repositoryRevision -notmatch '^[0-9a-fA-F]{40}$') {
            throw "Blind-judge sample '$($sample.Name)' has a repository revision that is not a full commit SHA."
        }
        Assert-Attempts -Attempts $witness.stableAttempts -Field 'stableAttempts' -SampleName $sample.Name
        Assert-Attempts -Attempts $witness.candidateAttempts -Field 'candidateAttempts' -SampleName $sample.Name
        if ([string]$witness.repositoryIdentity -match '^(?i:unavailable|local:)') {
            throw "Blind-judge sample '$($sample.Name)' does not contain an externally attributable repository identity."
        }
    }
}

$directory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
$assessment = [ordered]@{
    schemaVersion = 1
    outcome = 'PASS'
    method = 'Independent blind-judge completeness and attribution contract assessment'
    sampleCount = $samples.Count
    labelsOmitted = $true
    criteria = [ordered]@{
        samplesParse = $true
        witnessCompleteness = $true
        repositoryAttribution = $true
        controlAndCandidateConfigurationPresent = $true
        perAttemptEvidencePresent = $true
        reproductionCommandAndConfigurationPresent = $true
        normalizedFailureSignaturePresent = $true
        plantedLabelsAbsent = $true
        expectedOutcomesAbsent = $true
    }
}
($assessment | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Output "Blind-judge assessment passed for $($samples.Count) unlabeled sample(s) with complete attributable witnesses."
