[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $LicensePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$path = [IO.Path]::GetFullPath($LicensePath)
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "Release license not found: $path"
}

$license = Get-Item -LiteralPath $path
if (-not [string]::IsNullOrWhiteSpace([string] $license.LinkType) -or
    $license.Length -le 0 -or $license.Length -gt 1024 * 1024) {
    throw 'The release license must be a regular file between 1 byte and 1 MiB.'
}
$contents = Get-Content -LiteralPath $path -Raw
if ($contents -notmatch '(?m)^MIT License\s*$' -or
    $contents -notmatch '(?is)Permission is hereby granted, free of charge.*?publish, distribute, sublicense' -or
    $contents -notmatch '(?is)THE SOFTWARE IS PROVIDED "AS IS"') {
    throw 'The release license is not the repository-approved MIT license.'
}

$approvedSha256 = '5944250B546861E4E616DE520B7D06513FEC435A5651FC49D83AE92D3CF14BF2'
$actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
if ($actualHash -cne $approvedSha256) {
    throw "The release license differs from the repository-approved MIT text. Actual SHA-256: $actualHash"
}

Write-Host "Release license matched the repository-approved MIT text ($actualHash)."
