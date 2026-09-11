[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ExpectedVersion,
    [string] $ChangelogPath = 'CHANGELOG.md',
    [string] $ExpectedPackageVersion,
    [string] $ExpectedCommit,
    [string] $RepositoryPath = (Get-Location).Path
)

$ErrorActionPreference = 'Stop'

function Fail-Contract([string] $Message) {
    throw "Changelog contract failed: $Message"
}

function Normalize-Version([string] $Value, [string] $ParameterName) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        Fail-Contract "$ParameterName is required."
    }

    $normalized = $Value.Trim()
    if ($normalized.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring(1)
    }

    $versionPattern = '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'
    if ($normalized -notmatch $versionPattern) {
        Fail-Contract "$ParameterName '$Value' is not a valid release version."
    }

    return $normalized
}

function Invoke-Git([string[]] $Arguments) {
    try {
        $output = @(& git @Arguments 2>&1)
        $status = $LASTEXITCODE
    }
    catch {
        Fail-Contract "could not inspect the repository commit ($($_.Exception.Message))."
    }

    if ($status -ne 0) {
        Fail-Contract "could not inspect the repository commit (git exit code $status)."
    }

    return (($output | ForEach-Object { [string] $_ }) -join [Environment]::NewLine).Trim()
}

function Get-ContractFiles([string] $Root) {
    $excluded = @('\\.git\\', '\\bin\\', '\\obj\\', '\\artifacts\\')
    return @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force -ErrorAction Stop |
        Where-Object {
            $path = $_.FullName
            foreach ($fragment in $excluded) {
                if ($path -match [regex]::Escape($fragment)) { return $false }
            }
            return $_.Extension.ToLowerInvariant() -in @('.csproj', '.props', '.targets', '.md', '.json', '.ps1', '.sh', '.yml', '.yaml', '.txt')
        })
}

$releaseVersion = Normalize-Version $ExpectedVersion 'ExpectedVersion'
$packageVersion = $null
if (-not [string]::IsNullOrWhiteSpace($ExpectedPackageVersion)) {
    $packageVersion = Normalize-Version $ExpectedPackageVersion 'ExpectedPackageVersion'
    if ($packageVersion -ne $releaseVersion) {
        Fail-Contract "expected package version '$packageVersion' does not match release version '$releaseVersion'."
    }
}

try {
    $repository = (Resolve-Path -LiteralPath $RepositoryPath -ErrorAction Stop).Path
}
catch {
    Fail-Contract "repository path '$RepositoryPath' does not exist."
}

$head = Invoke-Git @('-C', $repository, 'rev-parse', '--verify', 'HEAD')
if (-not [string]::IsNullOrWhiteSpace($ExpectedCommit)) {
    $expectedCommitValue = $ExpectedCommit.Trim()
    if ($expectedCommitValue -notmatch '^[0-9a-fA-F]{40,64}$') {
        Fail-Contract "expected commit '$ExpectedCommit' is not a full Git commit ID."
    }
    if (-not [string]::Equals($head, $expectedCommitValue, [StringComparison]::OrdinalIgnoreCase)) {
        Fail-Contract "checked-out commit '$head' does not match expected commit '$expectedCommitValue'."
    }

    $status = Invoke-Git @('-C', $repository, 'status', '--porcelain', '--untracked-files=all')
    if (-not [string]::IsNullOrWhiteSpace($status)) {
        Fail-Contract 'the checked-out commit has a dirty working tree.'
    }
}

try {
    $changelog = (Resolve-Path -LiteralPath (Join-Path $repository $ChangelogPath) -ErrorAction Stop).Path
    $lines = [IO.File]::ReadAllLines($changelog)
}
catch {
    Fail-Contract "changelog '$ChangelogPath' could not be read."
}

$headingPattern = '^\s*(?<marks>#{1,6})\s+(?<text>.+?)\s*$'
$headings = @(
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $match = [regex]::Match($lines[$index], $headingPattern)
        if ($match.Success) {
            [pscustomobject]@{
                Index = $index
                Level = $match.Groups['marks'].Value.Length
                Text = $match.Groups['text'].Value.Trim()
            }
        }
    }
)

$versionToken = [regex]::Escape($releaseVersion)
$targetPattern = "(?<![0-9A-Za-z.-])v?$versionToken(?![0-9A-Za-z.-])"
$targets = @($headings | Where-Object { $_.Text -match $targetPattern })
if ($targets.Count -eq 0) {
    Fail-Contract "release version '$releaseVersion' is absent from '$ChangelogPath'."
}
if ($targets.Count -ne 1) {
    Fail-Contract "release version '$releaseVersion' must have exactly one release heading."
}
$target = $targets[0]

$preReleasePattern = '(?i)\b(?:planned|unreleased|not\s+yet\s+published|not\s+published|tbd|to\s+be\s+released|upcoming|forthcoming|draft|pending|pre[- ]?release)\b'
if ($target.Text -match $preReleasePattern) {
    Fail-Contract "release heading '$($target.Text)' still uses pre-release wording."
}

$ancestor = $null
foreach ($heading in $headings) {
    if ($heading.Index -ge $target.Index) { break }
    if ($heading.Level -lt $target.Level) { $ancestor = $heading }
}
if ($null -ne $ancestor -and $ancestor.Text -match '(?i)\b(?:unreleased|planned|upcoming|forthcoming|draft)\b') {
    Fail-Contract "release version '$releaseVersion' is inside the '$($ancestor.Text)' section."
}

$dateMatches = [regex]::Matches($target.Text, '(?<![0-9])\d{4}-\d{2}-\d{2}(?![0-9])')
if ($dateMatches.Count -ne 1) {
    Fail-Contract "release heading '$($target.Text)' must contain exactly one ISO release date."
}

[DateTime] $releaseDate = [DateTime]::MinValue
$dateText = $dateMatches[0].Value
if (-not [DateTime]::TryParseExact($dateText, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref] $releaseDate)) {
    Fail-Contract "release date '$dateText' is invalid."
}
if ($releaseDate.Date -gt [DateTime]::UtcNow.Date) {
    Fail-Contract "release date '$dateText' is later than the current UTC date."
}

$allFiles = Get-ContractFiles $repository
$versionDeclarations = @()
$propertyPattern = '<(?<name>CompatRadarReleaseVersion|PackageVersion|Version)(?:\s[^>]*)?>(?<value>[^<]+)</\k<name>>'
$selfPackagePattern = '<PackageVersion\b[^>]*Include\s*=\s*["'']KeelMatrix\.CompatRadar["''][^>]*Version\s*=\s*["''](?<value>[^"'']+)["'']'
foreach ($file in $allFiles | Where-Object { $_.Extension.ToLowerInvariant() -in @('.csproj', '.props', '.targets') }) {
    $content = [IO.File]::ReadAllText($file.FullName)
    foreach ($match in [regex]::Matches($content, $propertyPattern)) {
        $value = $match.Groups['value'].Value.Trim()
        if ($value -match '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
            $versionDeclarations += $value
        }
    }
    foreach ($match in [regex]::Matches($content, $selfPackagePattern)) {
        $versionDeclarations += $match.Groups['value'].Value.Trim()
    }
}

foreach ($declaredVersion in @($versionDeclarations | Select-Object -Unique)) {
    if ($declaredVersion -ne $releaseVersion) {
        Fail-Contract "repository-declared package version '$declaredVersion' does not match release version '$releaseVersion'."
    }
}

$packageReferencePattern = '(?i)(?<![0-9A-Za-z.-])v?(?<version>[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?)(?![0-9A-Za-z.-])'
$packageName = 'KeelMatrix.CompatRadar'
foreach ($file in $allFiles | Where-Object { $_.FullName -ne $changelog }) {
    $fileLines = [IO.File]::ReadAllLines($file.FullName)
    foreach ($line in $fileLines) {
        if ($line.IndexOf($packageName, [StringComparison]::OrdinalIgnoreCase) -lt 0) { continue }
        foreach ($match in [regex]::Matches($line, $packageReferencePattern)) {
            $referencedVersion = $match.Groups['version'].Value
            if ($referencedVersion -ne $releaseVersion) {
                Fail-Contract "package dependency or install example references '$packageName' at version '$referencedVersion', not '$releaseVersion'."
            }
        }
    }
}

Write-Output "Changelog contract passed: $packageName $releaseVersion at commit $head."
