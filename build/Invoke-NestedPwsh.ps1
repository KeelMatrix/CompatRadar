function Invoke-NestedPwsh {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0, ValueFromRemainingArguments = $true)]
        [string[]]$ArgumentList
    )

    $pwshArguments = [System.Collections.Generic.List[string]]::new()
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::Windows)) {
        [void]$pwshArguments.Add('-WindowStyle')
        [void]$pwshArguments.Add('Hidden')
    }

    foreach ($argument in $ArgumentList) {
        [void]$pwshArguments.Add($argument)
    }

    $pwshExecutable = 'pwsh'
    $nativeArguments = $pwshArguments.ToArray()
    & $pwshExecutable @nativeArguments
    $global:LASTEXITCODE = $LASTEXITCODE
}
