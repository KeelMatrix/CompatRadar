[CmdletBinding()]
param(
    [string] $RepositoryPath = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

function Fail-WebsiteCatalogContract([string] $Message) {
    throw "Website catalog metadata contract failed: $Message"
}

function Assert-ExactProperties([object] $Value, [string[]] $Expected, [string] $Location) {
    if ($null -eq $Value -or $null -eq $Value.PSObject) {
        Fail-WebsiteCatalogContract "$Location must be a JSON object."
    }

    if ($Value -is [System.Collections.IDictionary]) {
        $actual = @($Value.Keys | ForEach-Object { [string] $_ })
    }
    else {
        $actual = @($Value.PSObject.Properties | ForEach-Object { $_.Name })
    }
    if ($actual.Count -ne $Expected.Count) {
        Fail-WebsiteCatalogContract "$Location must contain only: $($Expected -join ', ')."
    }
    foreach ($name in $Expected) {
        $found = @($actual | Where-Object { [string]::Equals($_, $name, [StringComparison]::Ordinal) })
        if ($found.Count -ne 1) {
            Fail-WebsiteCatalogContract "$Location must contain '$name' exactly once."
        }
    }
}

function Get-EvaluatedProjectProperties([string] $ProjectPath) {
    $names = 'PackageId,Description,PackageTags,PackageProjectUrl,RepositoryUrl,RepositoryType,PackageType,PackAsTool,IsPackable'
    $output = @(& dotnet msbuild $ProjectPath -nologo "-getProperty:$names" 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        Fail-WebsiteCatalogContract "MSBuild could not evaluate '$ProjectPath' (exit $exitCode)."
    }

    try {
        $json = ($output | ForEach-Object { [string] $_ }) -join [Environment]::NewLine | ConvertFrom-Json -ErrorAction Stop
        return $json.Properties
    }
    catch {
        Fail-WebsiteCatalogContract "MSBuild returned invalid property data for '$ProjectPath'."
    }
}

try {
    $repository = (Resolve-Path -LiteralPath $RepositoryPath -ErrorAction Stop).Path
}
catch {
    Fail-WebsiteCatalogContract "repository path '$RepositoryPath' does not exist."
}

$manifestPath = Join-Path $repository 'keelmatrix.website.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    Fail-WebsiteCatalogContract 'root keelmatrix.website.json is missing.'
}

try {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable -ErrorAction Stop
}
catch {
    Fail-WebsiteCatalogContract 'root keelmatrix.website.json is not valid JSON.'
}

Assert-ExactProperties -Value $manifest -Expected @('schemaVersion', 'packages') -Location 'manifest root'
if (($manifest.schemaVersion -isnot [int] -and $manifest.schemaVersion -isnot [long]) -or $manifest.schemaVersion -ne 1) {
    Fail-WebsiteCatalogContract 'schemaVersion must be 1.'
}
if ($manifest.packages -isnot [System.Collections.IDictionary] -or $manifest.packages.Count -eq 0) {
    Fail-WebsiteCatalogContract 'manifest packages must be a non-empty JSON object.'
}

$manifestPackageIds = @($manifest.packages.Keys | Sort-Object -CaseSensitive)
$projects = @()
$projectFiles = @(Get-ChildItem -LiteralPath $repository -Recurse -File -Filter '*.csproj' -Force |
    Where-Object { $_.FullName -notmatch '[\\/](?:\.git|bin|obj|artifacts)[\\/]' })
foreach ($projectFile in $projectFiles) {
    $properties = Get-EvaluatedProjectProperties -ProjectPath $projectFile.FullName
    if ([string]::Equals([string]$properties.IsPackable, 'false', [StringComparison]::OrdinalIgnoreCase)) { continue }

    $packageId = [string]$properties.PackageId
    if ([string]::IsNullOrWhiteSpace($packageId)) { $packageId = $projectFile.BaseName }
    $projects += [pscustomobject]@{
        PackageId = $packageId
        ProjectPath = $projectFile.FullName
        Properties = $properties
    }
}

if ($projects.Count -eq 0) {
    Fail-WebsiteCatalogContract 'no packable project was found.'
}
$projectPackageIds = @($projects.PackageId | Sort-Object -CaseSensitive)
if (($manifestPackageIds -join "`n") -cne ($projectPackageIds -join "`n")) {
    Fail-WebsiteCatalogContract "manifest package IDs [$($manifestPackageIds -join ', ')] do not match packable project IDs [$($projectPackageIds -join ', ')]."
}

$knownVisibilityTags = @('keelmatrix-public-product', 'keelmatrix-internal-package')
$knownRoleTags = @('keelmatrix-primary', 'keelmatrix-component')
$canonicalRepositoryUrl = 'https://github.com/KeelMatrix/CompatRadar'
$validatedPackages = @()

foreach ($project in $projects) {
    $id = $project.PackageId
    $properties = $project.Properties
    $entry = $manifest.packages[$id]
    Assert-ExactProperties -Value $entry -Expected @('visibility', 'role') -Location "manifest entry '$id'"

    if ($entry.visibility -cnotin @('public-product', 'internal-package')) {
        Fail-WebsiteCatalogContract "manifest entry '$id' has unsupported visibility '$($entry.visibility)'."
    }
    if ($entry.role -cnotin @('primary', 'component')) {
        Fail-WebsiteCatalogContract "manifest entry '$id' has unsupported role '$($entry.role)'."
    }
    if ($id -ceq 'KeelMatrix.CompatRadar' -and ($entry.visibility -cne 'public-product' -or $entry.role -cne 'primary')) {
        Fail-WebsiteCatalogContract "'$id' must be public-product and primary."
    }

    if ([string]::IsNullOrWhiteSpace([string]$properties.Description)) {
        Fail-WebsiteCatalogContract "packable project '$id' must define a package description."
    }
    if ([string]$properties.PackageProjectUrl -cne $canonicalRepositoryUrl) {
        Fail-WebsiteCatalogContract "packable project '$id' must use PackageProjectUrl '$canonicalRepositoryUrl'."
    }
    if ([string]$properties.RepositoryUrl -cne $canonicalRepositoryUrl -or [string]$properties.RepositoryType -cne 'git') {
        Fail-WebsiteCatalogContract "packable project '$id' must use the same canonical GitHub RepositoryUrl and repository type 'git'."
    }

    $tags = @([regex]::Split([string]$properties.PackageTags, '[;\s]+') | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $visibilityMatches = @($tags | Where-Object { $knownVisibilityTags -icontains $_ })
    $visibilityExact = @($tags | Where-Object { $knownVisibilityTags -ccontains $_ })
    $roleMatches = @($tags | Where-Object { $knownRoleTags -icontains $_ })
    $roleExact = @($tags | Where-Object { $knownRoleTags -ccontains $_ })
    $expectedVisibilityTag = "keelmatrix-$($entry.visibility)"
    $expectedRoleTag = "keelmatrix-$($entry.role)"
    if ($visibilityMatches.Count -ne 1 -or $visibilityExact.Count -ne 1 -or $visibilityExact[0] -cne $expectedVisibilityTag) {
        Fail-WebsiteCatalogContract "packable project '$id' must contain exactly the matching lowercase visibility sentinel '$expectedVisibilityTag'."
    }
    if ($roleMatches.Count -ne 1 -or $roleExact.Count -ne 1 -or $roleExact[0] -cne $expectedRoleTag) {
        Fail-WebsiteCatalogContract "packable project '$id' must contain exactly the matching lowercase role sentinel '$expectedRoleTag'."
    }

    if ($id -ceq 'KeelMatrix.CompatRadar' -and (
            [string]$properties.PackageType -cne 'DotnetTool' -or
            -not [string]::Equals([string]$properties.PackAsTool, 'true', [StringComparison]::OrdinalIgnoreCase))) {
        Fail-WebsiteCatalogContract "'$id' must remain a DotnetTool NuGet package."
    }

    $validatedPackages += [pscustomobject]@{
        PackageId = $id
        Visibility = [string]$entry.visibility
        Role = [string]$entry.role
        ProjectUrl = [string]$properties.PackageProjectUrl
    }
}

foreach ($family in @($validatedPackages | Group-Object { $_.ProjectUrl.ToLowerInvariant() })) {
    $primary = @($family.Group | Where-Object { $_.Role -ceq 'primary' })
    if ($primary.Count -gt 1) {
        Fail-WebsiteCatalogContract "product family '$($family.Name)' has more than one primary package."
    }
    $publicProducts = @($family.Group | Where-Object { $_.Visibility -ceq 'public-product' })
    if ($publicProducts.Count -gt 0 -and @($publicProducts | Where-Object { $_.Role -ceq 'primary' }).Count -ne 1) {
        Fail-WebsiteCatalogContract "public product family '$($family.Name)' must have exactly one public primary package."
    }
}

Write-Output "Website catalog metadata contract passed: $($projects.Count) package(s), schema version 1."
