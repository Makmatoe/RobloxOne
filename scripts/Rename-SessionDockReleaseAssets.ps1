[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Directory,

    [Parameter(Mandatory)]
    [ValidatePattern('^win-x64-[a-z0-9-]+$')]
    [string] $Channel
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseJson.ps1')

$root = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "Release output not found: $root"
}

$renames = [ordered]@{
    "SessionDockApp-$Channel-Setup.exe" = 'SessionDock-win-x64-Setup.exe'
    "SessionDockApp-$Channel-Portable.zip" = 'SessionDock-win-x64-Portable.zip'
}
foreach ($entry in $renames.GetEnumerator()) {
    $source = Join-Path $root $entry.Key
    $destination = Join-Path $root $entry.Value
    if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or
        (Test-Path -LiteralPath $destination)) {
        throw "Release asset rename preconditions failed for '$($entry.Key)'."
    }
    Move-Item -LiteralPath $source -Destination $destination
}

$assetsPath = Join-Path $root "assets.$Channel.json"
$assets = @(ConvertFrom-ReleaseJson (Get-Content -LiteralPath $assetsPath -Raw))
if ($assets.Count -ne 3) {
    throw 'Velopack produced an unexpected release asset inventory.'
}
foreach ($asset in $assets) {
    $name = [string] $asset.RelativeFileName
    if ($renames.Contains($name)) {
        $asset.RelativeFileName = $renames[$name]
    }
}
foreach ($renamed in $renames.Values) {
    if (@($assets | Where-Object { $_.RelativeFileName -ceq $renamed }).Count -ne 1) {
        throw "Velopack asset inventory did not contain '$renamed' exactly once."
    }
}

$temporaryPath = "$assetsPath.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    $json = ConvertTo-Json -InputObject @($assets) -Depth 4 -Compress
    [IO.File]::WriteAllText(
        $temporaryPath,
        "$json`n",
        [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryPath -Destination $assetsPath -Force
}
finally {
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}

Write-Host 'Renamed public installer and portable assets for SessionDock.'
