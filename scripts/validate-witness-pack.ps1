[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackPath,
    [Parameter(Mandatory = $true)]
    [string] $OutputPath
)

# Structural validation for generated witness samples.
#
# This script proves that every sample parses, carries the required evidence, and exposes no
# outcome labels. It does not evaluate attribution or reproduction usefulness and does not read
# expectation data.

$ErrorActionPreference = 'Stop'
$samples = @(Get-ChildItem -LiteralPath $PackPath -Filter 'sample-*.json' -File | Sort-Object Name)
if ($samples.Count -eq 0) { throw 'Witness pack contains no samples.' }

$forbiddenProperties = @('planted', 'expected', 'expectedClassification', 'classification', 'stableControl', 'candidateResult', 'baseline', 'groundTruth', 'outcome', 'verdict', 'result')

function Assert-NonEmpty {
    param([object] $Value, [string] $Field, [string] $SampleName)

    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) {
        throw "Witness sample '$SampleName' has an empty required field '$Field'."
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
            throw "Witness sample '$SampleName' exposes the '$($property.Name)' label at '$Path.$($property.Name)'."
        }
        Assert-NoLabels -Value $property.Value -Path "$Path.$($property.Name)" -SampleName $SampleName
    }
}

function Assert-Attempts {
    param([object] $Attempts, [string] $Field, [string] $SampleName)

    if ($null -eq $Attempts) { throw "Witness sample '$SampleName' has no '$Field' attempt evidence." }
    $items = @($Attempts)
    if ($items.Count -eq 0) { throw "Witness sample '$SampleName' has empty '$Field' attempt evidence." }
    foreach ($attempt in $items) {
        foreach ($required in @('attempt', 'exitCode', 'summary', 'normalizedFailureSignature', 'fingerprint')) {
            if ($null -eq $attempt.PSObject.Properties[$required]) {
                throw "Witness sample '$SampleName' has '$Field' evidence without '$required'."
            }
            Assert-NonEmpty -Value $attempt.$required -Field "$Field.$required" -SampleName $SampleName
        }
    }
}

$witnessCount = 0
foreach ($sample in $samples) {
    $sampleObject = Get-Content -LiteralPath $sample.FullName -Raw | ConvertFrom-Json
    Assert-NoLabels -Value $sampleObject -Path '$' -SampleName $sample.Name
    if ($null -eq $sampleObject.witnesses) { throw "Witness sample '$($sample.Name)' has no witnesses." }
    $witnesses = @($sampleObject.witnesses)
    if ($witnesses.Count -eq 0) { throw "Witness sample '$($sample.Name)' has no witnesses." }
    foreach ($witness in $witnesses) {
        $witnessCount++
        foreach ($required in @(
                'candidate',
                'repositoryIdentity',
                'repositoryRevision',
                'repositoryContentHash',
                'controlConfiguration',
                'candidateInputConfiguration',
                'reproductionCommand',
                'reproductionConfiguration')) {
            if ($null -eq $witness.PSObject.Properties[$required]) {
                throw "Witness sample '$($sample.Name)' has a witness without '$required'."
            }
            Assert-NonEmpty -Value $witness.$required -Field $required -SampleName $sample.Name
        }
        Assert-Attempts -Attempts $witness.stableAttempts -Field 'stableAttempts' -SampleName $sample.Name
        Assert-Attempts -Attempts $witness.candidateAttempts -Field 'candidateAttempts' -SampleName $sample.Name
        if ([string]$witness.repositoryIdentity -match '^(?i:unavailable|local:)') {
            throw "Witness sample '$($sample.Name)' does not contain an externally attributable repository identity."
        }
        if ([string]$witness.repositoryRevision -notmatch '^([0-9a-fA-F]{40}|sha256:[0-9a-fA-F]{64})$') {
            throw "Witness sample '$($sample.Name)' does not identify the tested source revision or content."
        }
    }
}

$directory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
$structure = [ordered]@{
    schemaVersion = 1
    structurallyComplete = $true
    sampleCount = $samples.Count
    witnessCount = $witnessCount
    labelsOmitted = $true
    evaluationScope = 'structure-only'
    checks = [ordered]@{
        samplesParse = $true
        witnessCompleteness = $true
        repositoryAttribution = $true
        controlAndCandidateConfigurationPresent = $true
        reproductionCommandAndConfigurationPresent = $true
        perAttemptEvidencePresent = $true
        outcomeLabelsAbsent = $true
    }
    note = 'Structural completeness only. Attribution and reproduction usefulness are not evaluated here.'
}
($structure | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Output "Witness pack is structurally complete: $($samples.Count) sample(s), $witnessCount witness(es). Attribution and reproduction usefulness are outside this check."
