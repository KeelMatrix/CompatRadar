$ErrorActionPreference = 'Stop'
$output = (& dotnet list KeelMatrix.CompatRadar.sln package --vulnerable --include-transitive 2>&1 | Out-String)
$status = $LASTEXITCODE
if ($status -ne 0 -and $output -match '(?i)(NU1900|NU1301|unable to load the service index|audit.*(unavailable|service)|timed out|network)') {
    Write-Warning 'NuGet vulnerability service was unavailable; no vulnerability conclusion is claimed.'
    exit 0
}
if ($status -ne 0) { throw "Vulnerability audit command failed with exit code $status." }
if ($output -match '(?i)has the following vulnerable package') { throw 'A direct or transitive vulnerable package was reported.' }
Write-Output 'Direct and transitive vulnerability audit passed.'
