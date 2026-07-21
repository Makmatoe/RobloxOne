[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $LicensePath,

    [Parameter(Mandatory)]
    [string] $ApprovedSha256
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
if ($ApprovedSha256 -cnotmatch '^[0-9A-F]{64}$') {
    throw 'APPROVED_RELEASE_LICENSE_SHA256 must be exactly 64 uppercase hexadecimal characters.'
}

$contents = Get-Content -LiteralPath $path -Raw
if ($contents -match '(?is)no\s+(?:license|permission).*?\b(?:publish|distribute)\b' -or
    $contents -match '(?is)no\s+permission.*?\bredistribut') {
    throw 'The current repository license does not authorize release distribution. Adopt reviewed distributable terms before publishing.'
}

$actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
if ($actualHash -cne $ApprovedSha256) {
    throw "The release license hash is not the protected approved value. Actual SHA-256: $actualHash"
}

Write-Host "Release license approval matched SHA-256 $actualHash."
