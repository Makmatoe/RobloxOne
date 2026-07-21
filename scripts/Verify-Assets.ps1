[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Directory,

    [Parameter(Mandatory)]
    [string] $Manifest,

    [Parameter(Mandatory)]
    [string] $ExpectedPublisherSubject,

    [string] $ExpectedRepository = 'Makmatoe/RobloxOne',

    [string] $ExpectedChannel = 'win-x64-stable',

    [string] $ExpectedTag
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$directoryPath = [IO.Path]::GetFullPath($Directory)
$manifestPath = [IO.Path]::GetFullPath($Manifest)
if (-not (Test-Path -LiteralPath $directoryPath -PathType Container)) {
    throw "Release directory not found: $directoryPath"
}
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Release descriptor not found: $manifestPath"
}

$manifestInfo = Get-Item -LiteralPath $manifestPath
if ($manifestInfo.Length -le 0 -or $manifestInfo.Length -gt 131072) {
    throw 'Release descriptor must be between 1 byte and 128 KiB.'
}

$descriptor = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$requiredFields = @(
    'schemaVersion', 'product', 'repository', 'channel', 'keyId', 'version', 'tag',
    'publishedAt', 'packageFile', 'packageSize', 'packageSha256', 'releaseNotes', 'signature'
)
foreach ($field in $requiredFields) {
    if ($null -eq $descriptor.PSObject.Properties[$field]) {
        throw "Release descriptor is missing '$field'."
    }
}
$actualFields = @($descriptor.PSObject.Properties.Name)
$fieldDifferences = @(Compare-Object -ReferenceObject $requiredFields -DifferenceObject $actualFields -CaseSensitive)
if ($fieldDifferences.Count -ne 0 -or $actualFields.Count -ne $requiredFields.Count) {
    throw 'Release descriptor contains missing or unexpected fields.'
}

if ($descriptor.schemaVersion -ne 1 -or $descriptor.product -cne 'RobloxOne' -or
    $descriptor.keyId -cne 'robloxone-release-2026-01') {
    throw 'Descriptor schema, product, or signing key identifier is not recognized.'
}
if ($descriptor.repository -cne $ExpectedRepository) {
    throw "Descriptor repository '$($descriptor.repository)' does not match '$ExpectedRepository'."
}
if ($descriptor.channel -cne $ExpectedChannel) {
    throw "Descriptor channel '$($descriptor.channel)' does not match '$ExpectedChannel'."
}
if ($ExpectedTag -and $descriptor.tag -cne $ExpectedTag) {
    throw "Descriptor tag '$($descriptor.tag)' does not match '$ExpectedTag'."
}
if ($descriptor.version -cnotmatch '^\d+\.\d+\.\d+$' -or $descriptor.tag -cne "v$($descriptor.version)") {
    throw 'Descriptor version and tag are not aligned stable versions.'
}
if ($descriptor.packageFile -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]*-full\.nupkg$') {
    throw "Descriptor package filename is unsafe or not a full package: $($descriptor.packageFile)"
}
if ([string]::IsNullOrWhiteSpace([string] $descriptor.signature)) {
    throw 'Release descriptor has no signature.'
}

$packagePath = [IO.Path]::GetFullPath((Join-Path $directoryPath ([string] $descriptor.packageFile)))
if (-not $packagePath.StartsWith($directoryPath.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Descriptor package path escapes the release directory.'
}
if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
    throw "Descriptor package does not exist: $packagePath"
}

$packageInfo = Get-Item -LiteralPath $packagePath
if ([long] $descriptor.packageSize -ne $packageInfo.Length) {
    throw 'Descriptor package size does not match the full package.'
}
$actualHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
if ($actualHash -cne ([string] $descriptor.packageSha256).ToUpperInvariant()) {
    throw 'Descriptor package SHA-256 does not match the full package.'
}

$executables = @(Get-ChildItem -LiteralPath $directoryPath -File -Filter '*.exe')
if ($executables.Count -eq 0) {
    throw 'Release contains no signed executable.'
}
foreach ($executable in $executables) {
    $authenticode = Get-AuthenticodeSignature -LiteralPath $executable.FullName
    if ($authenticode.Status -ne [Management.Automation.SignatureStatus]::Valid) {
        throw "Authenticode validation failed for $($executable.Name): $($authenticode.StatusMessage)"
    }
    if ($null -eq $authenticode.SignerCertificate -or
        $authenticode.SignerCertificate.Subject -cne $ExpectedPublisherSubject) {
        throw "Unexpected Authenticode publisher for $($executable.Name)."
    }
}

$extractionDirectory = Join-Path ([IO.Path]::GetTempPath()) ("robloxone-package-" + [Guid]::NewGuid().ToString('N'))
try {
    [System.IO.Compression.ZipFile]::ExtractToDirectory($packagePath, $extractionDirectory)
    $packagedApplication = @(Get-ChildItem -LiteralPath $extractionDirectory -Recurse -File -Filter 'RobloxOne.exe')
    if ($packagedApplication.Count -ne 1) {
        throw "Expected exactly one RobloxOne.exe in the full package; found $($packagedApplication.Count)."
    }
    $applicationSignature = Get-AuthenticodeSignature -LiteralPath $packagedApplication[0].FullName
    if ($applicationSignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $applicationSignature.SignerCertificate -or
        $applicationSignature.SignerCertificate.Subject -cne $ExpectedPublisherSubject) {
        throw 'The packaged RobloxOne.exe does not have the expected valid Authenticode signature.'
    }
}
finally {
    if (Test-Path -LiteralPath $extractionDirectory) {
        Remove-Item -LiteralPath $extractionDirectory -Recurse -Force
    }
}

$portablePackages = @(Get-ChildItem -LiteralPath $directoryPath -File -Filter '*-Portable.zip')
if ($portablePackages.Count -ne 1) {
    throw "Expected exactly one portable ZIP; found $($portablePackages.Count)."
}
$portableExtractionDirectory = Join-Path ([IO.Path]::GetTempPath()) ("robloxone-portable-" + [Guid]::NewGuid().ToString('N'))
try {
    [System.IO.Compression.ZipFile]::ExtractToDirectory(
        $portablePackages[0].FullName,
        $portableExtractionDirectory)
    $portableApplication = @(Get-ChildItem -LiteralPath $portableExtractionDirectory -Recurse -File -Filter 'RobloxOne.exe')
    if ($portableApplication.Count -ne 1) {
        throw "Expected exactly one RobloxOne.exe in the portable ZIP; found $($portableApplication.Count)."
    }
    $portableSignature = Get-AuthenticodeSignature -LiteralPath $portableApplication[0].FullName
    if ($portableSignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $portableSignature.SignerCertificate -or
        $portableSignature.SignerCertificate.Subject -cne $ExpectedPublisherSubject) {
        throw 'The portable RobloxOne.exe does not have the expected valid Authenticode signature.'
    }
}
finally {
    if (Test-Path -LiteralPath $portableExtractionDirectory) {
        Remove-Item -LiteralPath $portableExtractionDirectory -Recurse -Force
    }
}

Write-Host "Verified descriptor, package hash, installer, full package, and portable Authenticode signatures."
