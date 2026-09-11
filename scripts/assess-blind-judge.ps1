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

foreach ($sample in $samples) {
    $sampleObject = Get-Content -LiteralPath $sample.FullName -Raw | ConvertFrom-Json
    if ($null -eq $sampleObject.witnesses) { throw "Blind-judge sample '$($sample.Name)' has no witnesses." }
    foreach ($witness in @($sampleObject.witnesses)) {
        if ([string]::IsNullOrWhiteSpace([string]$witness.candidate) -or [string]::IsNullOrWhiteSpace([string]$witness.summary)) {
            throw "Blind-judge sample '$($sample.Name)' has an incomplete witness."
        }
        foreach ($forbiddenProperty in @('planted', 'expected', 'classification', 'stableControl', 'baseline')) {
            if ($null -ne $witness.PSObject.Properties[$forbiddenProperty]) { throw "Blind-judge sample '$($sample.Name)' exposes the '$forbiddenProperty' label." }
        }
    }
}

$directory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
$assessment = [ordered]@{
    schemaVersion = 1
    outcome = 'PASS'
    method = 'Independent blind-judge contract assessment'
    sampleCount = $samples.Count
    labelsOmitted = $true
    criteria = [ordered]@{
        samplesParse = $true
        reproductionWitnessesPresent = $true
        plantedLabelsAbsent = $true
        expectedOutcomesAbsent = $true
    }
}
($assessment | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Output "Blind-judge assessment passed for $($samples.Count) unlabeled sample(s)."
