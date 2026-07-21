[CmdletBinding()]
param(
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$localAppData = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::LocalApplicationData)
if ([string]::IsNullOrWhiteSpace($localAppData)) {
    throw 'The current Windows user does not have a local application-data directory.'
}

$directory = Join-Path $localAppData 'SessionDock'
if (Test-Path -LiteralPath $directory) {
    $directoryInfo = Get-Item -LiteralPath $directory -Force
    if (-not $directoryInfo.PSIsContainer -or
        -not [string]::IsNullOrWhiteSpace([string] $directoryInfo.LinkType) -or
        ($directoryInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The SessionDock data path is not a regular directory.'
    }
}
else {
    New-Item -ItemType Directory -Path $directory -ErrorAction Stop | Out-Null
}

$destination = Join-Path $directory 'handlescope.json'
$destinationExists = Test-Path -LiteralPath $destination
if ($destinationExists) {
    $existing = Get-Item -LiteralPath $destination -Force
    if (-not [string]::IsNullOrWhiteSpace([string] $existing.LinkType) -or
        ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $existing.PSIsContainer) {
        throw 'The HandleScope configuration path is not a regular file.'
    }
    if (-not $Force) {
        throw 'HandleScope configuration already exists. Review it or rerun with -Force to replace it with the minimal opt-in.'
    }
}

$temporary = Join-Path $directory (".handlescope-{0}.tmp" -f [Guid]::NewGuid().ToString('N'))
try {
    [IO.File]::WriteAllText(
        $temporary,
        "{`"enabled`":true}`n",
        [Text.UTF8Encoding]::new($false))
    if ($destinationExists) {
        [IO.File]::Replace($temporary, $destination, $null, $true)
    }
    else {
        [IO.File]::Move($temporary, $destination)
    }
}
finally {
    Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
}

Write-Host "HandleScope integration enabled for the current user: $destination"
Write-Host 'Start the separately installed HandleScope v1 local API before launching Roblox.'
