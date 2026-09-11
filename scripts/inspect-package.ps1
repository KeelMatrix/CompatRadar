[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackageDirectory,
    [Parameter(Mandatory = $true)]
    [string] $ExpectedVersion,
    [string] $ExpectedCommit
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$directory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$id = 'KeelMatrix.CompatRadar'
$expected = @("$id.$ExpectedVersion.nupkg", "$id.$ExpectedVersion.snupkg") | Sort-Object
$actual = @(Get-ChildItem -LiteralPath $directory -File | Select-Object -ExpandProperty Name | Sort-Object)
if (($expected -join "`n") -ne ($actual -join "`n")) {
    throw "Unexpected package set. Expected '$($expected -join ', ')' but found '$($actual -join ', ')'."
}

function Get-EntryText($archive, [string] $name) {
    $entry = $archive.GetEntry($name)
    if ($null -eq $entry) { throw "Required package entry '$name' is missing." }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Get-IconDimensions($archive) {
    $entry = $archive.GetEntry('icon.png')
    if ($null -eq $entry) { throw 'icon.png is missing.' }
    $stream = $entry.Open()
    try {
        $bytes = [byte[]]::new(24)
        if ($stream.Read($bytes, 0, $bytes.Length) -ne $bytes.Length) { throw 'icon.png is truncated.' }
        $signature = [BitConverter]::ToString($bytes[0..7])
        if ($signature -ne '89-50-4E-47-0D-0A-1A-0A') { throw 'icon.png is not a PNG.' }
        $width = [System.Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 16))
        $height = [System.Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 20))
        return @($width, $height)
    } finally { $stream.Dispose() }
}

function Assert-ExactArchiveEntries($archive, [string] $packageKind) {
    $allowed = if ($packageKind -eq 'nupkg') {
        @(
            '^_rels/\.rels$',
            '^\[Content_Types\]\.xml$',
            '^icon\.png$',
            '^KeelMatrix\.CompatRadar\.nuspec$',
            '^LICENSE$',
            '^package/services/metadata/core-properties/[0-9a-f]{32}\.psmdcp$',
            '^README\.md$',
            '^tools/net8\.0/any/DotnetToolSettings\.xml$',
            '^tools/net8\.0/any/KeelMatrix\.CompatRadar\.deps\.json$',
            '^tools/net8\.0/any/KeelMatrix\.CompatRadar\.dll$',
            '^tools/net8\.0/any/KeelMatrix\.CompatRadar\.pdb$',
            '^tools/net8\.0/any/KeelMatrix\.CompatRadar\.runtimeconfig\.json$',
            '^tools/net8\.0/any/KeelMatrix\.CompatRadar\.xml$',
            '^tools/net8\.0/any/KeelMatrix\.Telemetry\.dll$'
        )
    } elseif ($packageKind -eq 'snupkg') {
        @(
            '^_rels/\.rels$',
            '^\[Content_Types\]\.xml$',
            '^KeelMatrix\.CompatRadar\.nuspec$',
            '^package/services/metadata/core-properties/[0-9a-f]{32}\.psmdcp$',
            '^tools/net8\.0/any/KeelMatrix\.CompatRadar\.pdb$'
        )
    } else {
        throw "Unknown package kind '$packageKind'."
    }

    $names = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    $duplicates = @($names | Group-Object | Where-Object Count -gt 1 | Select-Object -ExpandProperty Name)
    if ($duplicates.Count -gt 0) { throw "Duplicate package entries in $packageKind`: $($duplicates -join ', ')." }
    foreach ($name in $names) {
        if (-not (@($allowed | Where-Object { $name -match $_ }).Count -gt 0)) {
            throw "Unexpected package entry in $packageKind`: '$name'."
        }
    }
}

$nupkgPath = Join-Path $directory "$id.$ExpectedVersion.nupkg"
$symbolPath = Join-Path $directory "$id.$ExpectedVersion.snupkg"
$nupkg = [IO.Compression.ZipFile]::OpenRead($nupkgPath)
$symbols = [IO.Compression.ZipFile]::OpenRead($symbolPath)
try {
    Assert-ExactArchiveEntries $nupkg 'nupkg'
    Assert-ExactArchiveEntries $symbols 'snupkg'
    $nuspecEntry = @($nupkg.Entries | Where-Object { $_.FullName -match "(^|/)$id\.nuspec$" }) | Select-Object -First 1
    if ($null -eq $nuspecEntry) { throw 'The package nuspec is missing.' }
    $reader = [IO.StreamReader]::new($nuspecEntry.Open())
    try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $metadata = $nuspec.package.metadata
    if ($metadata.id -ne $id -or $metadata.version -ne $ExpectedVersion) { throw 'Package ID/version metadata mismatch.' }
    if ($metadata.authors -ne 'KeelMatrix') { throw 'Authors metadata mismatch.' }
    if ($metadata.license.'#text' -ne 'MIT' -or $metadata.license.type -ne 'expression') { throw 'License metadata mismatch.' }
    if ([string]::IsNullOrWhiteSpace([string]$metadata.description)) { throw 'Description metadata is missing.' }
    foreach ($tag in @('dotnet', 'runtime', 'nuget', 'compatibility', 'regression-testing')) {
        if (-not ([string]$metadata.tags).Split(' ', [StringSplitOptions]::RemoveEmptyEntries).Contains($tag)) { throw "Required package tag '$tag' is missing." }
    }
    $repository = $metadata.repository
    if ($repository.url -ne 'https://github.com/KeelMatrix/CompatRadar') { throw 'Repository URL metadata mismatch.' }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedCommit) -and $repository.commit -ne $ExpectedCommit) { throw 'Repository commit metadata mismatch.' }
    $dependency = @($metadata.dependencies.group.dependency | Where-Object { $_.id -eq 'KeelMatrix.Telemetry' })
    $bundledTelemetry = $null -ne $nupkg.GetEntry('tools/net8.0/any/KeelMatrix.Telemetry.dll')
    if ($dependency.Count -ne 1 -and -not $bundledTelemetry) { throw 'KeelMatrix.Telemetry is neither a declared dependency nor a bundled runtime asset.' }

    foreach ($required in @('README.md', 'LICENSE', 'icon.png')) {
        if ($null -eq $nupkg.GetEntry($required)) { throw "Required package entry '$required' is missing." }
    }
    $icon = Get-IconDimensions $nupkg
    if ($icon[0] -ne 512 -or $icon[1] -ne 512) { throw "Icon dimensions $($icon[0])x$($icon[1]) do not match the 512x512 package contract." }
    if ($nupkg.GetEntry('icon.png').Length -gt 204800) { throw 'icon.png exceeds the 200 KB package limit.' }

    $toolSettings = @($nupkg.Entries | Where-Object { $_.FullName -match '(^|/)DotnetToolSettings\.xml$' }) | Select-Object -First 1
    if ($null -eq $toolSettings) { throw 'DotnetToolSettings.xml is missing.' }
    $toolText = Get-EntryText $nupkg $toolSettings.FullName
    if ($toolText -notmatch '(?i)compat-radar') { throw 'DotnetToolSettings.xml does not declare compat-radar.' }
    foreach ($requiredAsset in @('tools/net8.0/any/KeelMatrix.CompatRadar.dll', 'tools/net8.0/any/KeelMatrix.CompatRadar.deps.json', 'tools/net8.0/any/KeelMatrix.CompatRadar.runtimeconfig.json')) {
        if ($null -eq $nupkg.GetEntry($requiredAsset)) { throw "Required tool asset '$requiredAsset' is missing." }
    }
    if (@($symbols.Entries | Where-Object { $_.FullName -match '\.pdb$' }).Count -eq 0) { throw 'Symbols package contains no PDB.' }
    $pdbEntry = @($symbols.Entries | Where-Object { $_.FullName -match '\.pdb$' }) | Select-Object -First 1
    $pdbStream = $pdbEntry.Open()
    $pdbMemory = [IO.MemoryStream]::new()
    try { $pdbStream.CopyTo($pdbMemory) } finally { $pdbStream.Dispose() }
    $pdbText = [Text.Encoding]::UTF8.GetString($pdbMemory.ToArray())
    if ($pdbText -notmatch 'raw\.githubusercontent\.com/KeelMatrix/CompatRadar') { throw 'Symbols package does not contain SourceLink evidence.' }

    $forbidden = '(?i)(^|/)(AGENTS\.md|CONTRIBUTING\.md|SECURITY\.md|PRIVACY\.md|tests?|fixtures?|artifacts|obj|bin|\.git|\.vs|\.vscode|\.env(?:\..*)?|local-telemetry|_probe)(/|$)'
    foreach ($entry in $nupkg.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        if ($name -match $forbidden) { throw "Forbidden package entry '$name'." }
    }
}
finally {
    $symbols.Dispose()
    $nupkg.Dispose()
}

Write-Output "Package contract passed: $id $ExpectedVersion (icon $($icon[0])x$($icon[1]), $((Get-Item $nupkgPath).Length) bytes)."
