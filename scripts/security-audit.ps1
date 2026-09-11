[CmdletBinding()]
param(
    [string] $CommandPath = 'dotnet',
    [string] $CommandArgumentsJson,
    [string[]] $CommandArguments = @(
        'list',
        'KeelMatrix.CompatRadar.sln',
        'package',
        '--vulnerable',
        '--include-transitive',
        '--format',
        'json',
        '--output-version',
        '1'
    )
)

$ErrorActionPreference = 'Stop'

function Test-AdvisoryUnavailable([string] $Text) {
    return $Text -match '(?i)(NU1900|NU1301|NU1302|unable to load the service index|vulnerability (?:data|information|audit source).*(?:unavailable|failed|could not)|audit.*(?:unavailable|failed|service)|(?:http|status).*(?:401|403|408|429|500|502|503|504)|timed out|timeout|network|name resolution|connection refused|no such host|temporary failure|unable to reach)'
}

function Get-VulnerabilityCount($Value) {
    if ($null -eq $Value) { return 0 }

    $count = 0
    foreach ($property in @($Value.PSObject.Properties)) {
        if ($property.Name -ieq 'vulnerabilities') {
            $items = $property.Value
            if ($null -eq $items) { continue }
            if ($items -is [string]) {
                if (-not [string]::IsNullOrWhiteSpace($items)) { $count++ }
            } elseif ($items -is [System.Collections.IEnumerable]) {
                $count += @($items).Count
            } else {
                $count++
            }
            continue
        }

        $child = $property.Value
        if ($null -ne $child -and $child -isnot [string]) {
            if ($child -is [System.Collections.IEnumerable]) {
                foreach ($item in $child) { $count += Get-VulnerabilityCount $item }
            } else {
                $count += Get-VulnerabilityCount $child
            }
        }
    }
    return $count
}

function Assert-AuditContract($Report) {
    if ($null -eq $Report) { throw 'empty audit output' }
    if ($Report.version -ne 1) { throw 'unrecognized audit schema version' }
    if ([string]$Report.parameters -notmatch '(?=.*--vulnerable)(?=.*--include-transitive)') { throw 'audit was not direct-and-transitive vulnerability mode' }
    $sources = @($Report.sources)
    if ($sources.Count -eq 0 -or ($sources | Where-Object { [string]::IsNullOrWhiteSpace([string]$_) }).Count -gt 0) { throw 'audit report has no usable sources' }
    $projects = @($Report.projects)
    if ($projects.Count -eq 0) { throw 'audit report has no projects' }
    foreach ($project in $projects) {
        if ([string]::IsNullOrWhiteSpace([string]$project.path)) { throw 'audit report has a project without a path' }
    }
}

try {
    if (-not [string]::IsNullOrWhiteSpace($CommandArgumentsJson)) {
        $CommandArguments = @($CommandArgumentsJson | ConvertFrom-Json)
    }
    try {
        $output = (& $CommandPath @CommandArguments 2>&1 | Out-String)
        $commandStatus = $LASTEXITCODE
    } catch {
        [Console]::Error.WriteLine("Dependency vulnerability audit status unavailable: command failure ($($_.Exception.GetType().Name)).")
        exit 2
    }

    if (Test-AdvisoryUnavailable $output) {
        [Console]::Error.WriteLine('Dependency vulnerability audit status unavailable: advisory service or network failure.')
        exit 2
    }
    if ([string]::IsNullOrWhiteSpace($output)) {
        [Console]::Error.WriteLine('Dependency vulnerability audit status unavailable: empty audit output.')
        exit 2
    }

    try {
        $report = $output | ConvertFrom-Json
        Assert-AuditContract $report
    } catch {
        if ($commandStatus -ne 0) {
            [Console]::Error.WriteLine("Dependency vulnerability audit status unavailable: command failure with exit code $commandStatus.")
        } else {
            [Console]::Error.WriteLine('Dependency vulnerability audit status unavailable: unrecognized or malformed audit output.')
        }
        exit 2
    }

    $vulnerabilityCount = Get-VulnerabilityCount $report
    if ($vulnerabilityCount -gt 0) {
        [Console]::Error.WriteLine("Dependency vulnerability audit failed: $vulnerabilityCount direct or transitive vulnerability record(s) reported.")
        exit 1
    }
    if ($commandStatus -ne 0) {
        [Console]::Error.WriteLine("Dependency vulnerability audit status unavailable: command failed with exit code $commandStatus.")
        exit 2
    }

    Write-Output 'Direct and transitive vulnerability audit passed: valid JSON schema v1 with no vulnerability records.'
    exit 0
} catch {
    [Console]::Error.WriteLine("Dependency vulnerability audit status unavailable: $($_.Exception.Message)")
    exit 2
}
