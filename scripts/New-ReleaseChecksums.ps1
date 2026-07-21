[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Directory,

    [string] $Output = 'SHA256SUMS.txt'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$directoryPath = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $directoryPath -PathType Container)) {
    throw "Release directory not found: $directoryPath"
}
if ([IO.Path]::GetFileName($Output) -cne $Output -or $Output -cne 'SHA256SUMS.txt') {
    throw 'The checksum output must be the top-level file SHA256SUMS.txt.'
}

$outputPath = Join-Path $directoryPath $Output
if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Force
}
$directories = @(Get-ChildItem -LiteralPath $directoryPath -Directory -Force)
if ($directories.Count -ne 0) {
    throw 'Release output must contain top-level files only before checksums are generated.'
}

$files = @(Get-ChildItem -LiteralPath $directoryPath -File -Force | Sort-Object Name)
if ($files.Count -eq 0) {
    throw 'No release assets are available for checksumming.'
}
foreach ($file in $files) {
    if ($file.Name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or
        -not [string]::IsNullOrWhiteSpace([string] $file.LinkType)) {
        throw "Release asset has an unsafe name or file type: $($file.Name)"
    }
}

$lines = foreach ($file in $files) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($file.Name)"
}
[IO.File]::WriteAllLines(
    $outputPath,
    $lines,
    [Text.Encoding]::ASCII)
Write-Host "Wrote SHA-256 checksums for $($files.Count) release assets."
