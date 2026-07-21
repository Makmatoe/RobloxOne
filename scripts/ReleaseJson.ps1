Set-StrictMode -Version Latest

function ConvertFrom-ReleaseJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string] $Json
    )

    $command = Get-Command ConvertFrom-Json -CommandType Cmdlet
    if ($command.Parameters.ContainsKey('DateKind')) {
        return (ConvertFrom-Json -InputObject $Json -DateKind String)
    }
    return (ConvertFrom-Json -InputObject $Json)
}
