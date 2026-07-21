[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Directory,

    [Parameter(Mandatory)]
    [string] $Manifest,

    [Parameter(Mandatory)]
    [string] $PublishedApplicationDirectory,

    [Parameter(Mandatory)]
    [string] $ExpectedPublisherSubject,

    [Parameter(Mandatory)]
    [string] $ApprovedReleaseLicenseSha256,

    [string] $ExpectedRepository = 'Makmatoe/RobloxOne',

    [string] $ExpectedChannel = 'win-x64-stable',

    [string] $ExpectedTag
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-RelativeFiles([string] $Root) {
    $trimmedRoot = $Root.TrimEnd('\', '/')
    return @(Get-ChildItem -LiteralPath $trimmedRoot -Recurse -File -Force |
        ForEach-Object {
            $_.FullName.Substring($trimmedRoot.Length + 1).Replace('\', '/')
        } | Sort-Object)
}

function Assert-ExactSet(
    [string[]] $Expected,
    [string[]] $Actual,
    [string] $Description) {
    $differences = @(Compare-Object `
        -ReferenceObject @($Expected | Sort-Object) `
        -DifferenceObject @($Actual | Sort-Object) `
        -CaseSensitive)
    if ($differences.Count -ne 0 -or $Expected.Count -ne $Actual.Count) {
        throw "$Description contains missing or unexpected entries:`n$($differences | Out-String)"
    }
}

function Assert-Authenticode([string] $Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
        throw "Authenticode validation failed for $([IO.Path]::GetFileName($Path)): $($signature.StatusMessage)"
    }
    if ($null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -cne $ExpectedPublisherSubject) {
        throw "Unexpected Authenticode publisher for $([IO.Path]::GetFileName($Path))."
    }
}

function Get-NormalizedNotes([string] $Value) {
    return $Value.Replace("`r`n", "`n").Replace("`r", "`n").Trim()
}

function Assert-FileHashEqual([string] $Expected, [string] $Actual, [string] $Description) {
    $expectedHash = (Get-FileHash -LiteralPath $Expected -Algorithm SHA256).Hash
    $actualHash = (Get-FileHash -LiteralPath $Actual -Algorithm SHA256).Hash
    if ($expectedHash -cne $actualHash) {
        throw "$Description does not match the verified publish input."
    }
}

function Assert-ExecutableVersion(
    [string] $Path,
    [string] $ExpectedVersion) {
    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    if ($versionInfo.FileVersion -cne "$ExpectedVersion.0" -or
        $versionInfo.ProductVersion -cnotmatch
            ('^' + [regex]::Escape($ExpectedVersion) + '(\+[0-9a-f]{40})?$')) {
        throw "Unexpected executable version for $([IO.Path]::GetFileName($Path))."
    }
}

function Get-XmlChildText([Xml.XmlElement] $Parent, [string] $Name) {
    $node = $Parent.SelectSingleNode("*[local-name()='$Name']")
    if ($null -eq $node) {
        throw "Velopack package metadata is missing '$Name'."
    }
    return [string] $node.InnerText
}

$directoryPath = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
$manifestPath = [IO.Path]::GetFullPath($Manifest)
$applicationPath = [IO.Path]::GetFullPath($PublishedApplicationDirectory).TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $directoryPath -PathType Container)) {
    throw "Release directory not found: $directoryPath"
}
if (-not (Test-Path -LiteralPath $applicationPath -PathType Container)) {
    throw "Published application directory not found: $applicationPath"
}
$expectedManifestPath = Join-Path $directoryPath 'robloxone-release.json'
if (-not $manifestPath.Equals($expectedManifestPath, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw 'The release descriptor must be the top-level robloxone-release.json asset.'
}
if ([string]::IsNullOrWhiteSpace($ExpectedPublisherSubject) -or
    $ExpectedPublisherSubject.Length -gt 512 -or
    $ExpectedPublisherSubject -match '[\x00-\x1F\x7F]') {
    throw 'The expected Authenticode publisher subject is invalid.'
}

$releaseItems = @(Get-ChildItem -LiteralPath $directoryPath -Force)
if ($releaseItems | Where-Object {
        $_.PSIsContainer -or
        -not [string]::IsNullOrWhiteSpace([string] $_.LinkType)
    }) {
    throw 'Release output must contain regular top-level files only.'
}
$applicationItems = @(Get-ChildItem -LiteralPath $applicationPath -Recurse -Force)
if ($applicationItems | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string] $_.LinkType)
    }) {
    throw 'Published application input must not contain reparse points.'
}

$expectedApplicationFiles = @(
    'LICENSE.md',
    'RobloxOne.exe',
    'THIRD_PARTY_NOTICES.md',
    'licenses/DotNet-LICENSE.txt',
    'licenses/DotNet-THIRD-PARTY-NOTICES.txt',
    'licenses/Microsoft.Web.WebView2-LICENSE.txt',
    'licenses/Microsoft.Web.WebView2-NOTICE.txt',
    'licenses/Microsoft.WindowsDesktop-LICENSE.txt',
    'licenses/Velopack-LICENSE.txt'
)
$sourceComparableApplicationFiles = @(
    $expectedApplicationFiles | Where-Object { $_ -cne 'RobloxOne.exe' })
Assert-ExactSet `
    -Expected $expectedApplicationFiles `
    -Actual (Get-RelativeFiles $applicationPath) `
    -Description 'Published application input'
& (Join-Path $PSScriptRoot 'Verify-ReleaseLicense.ps1') `
    -LicensePath (Join-Path $applicationPath 'LICENSE.md') `
    -ApprovedSha256 $ApprovedReleaseLicenseSha256

$manifestInfo = Get-Item -LiteralPath $manifestPath
if ($manifestInfo.Length -le 0 -or $manifestInfo.Length -gt 128 * 1024) {
    throw 'Release descriptor must be between 1 byte and 128 KiB.'
}
$descriptor = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$requiredDescriptorFields = @(
    'schemaVersion', 'product', 'repository', 'channel', 'keyId', 'version', 'tag',
    'publishedAt', 'packageFile', 'packageSize', 'packageSha256', 'releaseNotes', 'signature'
)
$actualDescriptorFields = @($descriptor.PSObject.Properties.Name)
Assert-ExactSet `
    -Expected $requiredDescriptorFields `
    -Actual $actualDescriptorFields `
    -Description 'Release descriptor'
if ($descriptor.schemaVersion -ne 1 -or
    $descriptor.product -cne 'RobloxOne' -or
    $descriptor.keyId -cne 'robloxone-release-2026-01' -or
    $descriptor.repository -cne $ExpectedRepository -or
    $descriptor.channel -cne $ExpectedChannel) {
    throw 'Descriptor schema, product, repository, channel, or signing key is not recognized.'
}
if ($descriptor.version -cnotmatch '^\d+\.\d+\.\d+$' -or
    $descriptor.tag -cne "v$($descriptor.version)" -or
    ($ExpectedTag -and $descriptor.tag -cne $ExpectedTag)) {
    throw 'Descriptor version and tag are not aligned stable versions.'
}
try {
    $publishedAt = [DateTimeOffset]::ParseExact(
        [string] $descriptor.publishedAt,
        'O',
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind)
}
catch [FormatException] {
    throw 'Descriptor publication time is invalid.'
}
if ($publishedAt.Offset -ne [TimeSpan]::Zero -or
    $publishedAt -gt [DateTimeOffset]::UtcNow.AddHours(24)) {
    throw 'Descriptor publication time is invalid.'
}
if ([string]::IsNullOrWhiteSpace([string] $descriptor.releaseNotes) -or
    $descriptor.releaseNotes.Length -gt 64 * 1024 -or
    $descriptor.releaseNotes.Contains("`r") -or
    $descriptor.releaseNotes -match '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]') {
    throw 'Descriptor release notes are invalid.'
}
if ($descriptor.packageSha256 -cnotmatch '^[0-9A-F]{64}$' -or
    $descriptor.packageSize -lt 1024 * 1024 -or
    $descriptor.packageSize -gt 1024L * 1024 * 1024) {
    throw 'Descriptor package digest or size is invalid.'
}
try {
    $signatureBytes = [Convert]::FromBase64String([string] $descriptor.signature)
}
catch [FormatException] {
    throw 'Descriptor signature is not valid Base64.'
}
if ($signatureBytes.Length -ne 64) {
    throw 'Descriptor signature must be one P-256 signature.'
}

$packageName = "RobloxOne-$($descriptor.version)-$ExpectedChannel-full.nupkg"
$portableName = "RobloxOne-$ExpectedChannel-Portable.zip"
$setupName = "RobloxOne-$ExpectedChannel-Setup.exe"
$sbomName = "RobloxOne-$($descriptor.version)-sbom.spdx.json"
if ($descriptor.packageFile -cne $packageName) {
    throw 'Descriptor package filename does not match the exact release convention.'
}
$expectedReleaseFiles = @(
    "RELEASES-$ExpectedChannel",
    'SHA256SUMS.txt',
    "assets.$ExpectedChannel.json",
    $packageName,
    $sbomName,
    $portableName,
    $setupName,
    "releases.$ExpectedChannel.json",
    'robloxone-release.json'
)
Assert-ExactSet `
    -Expected $expectedReleaseFiles `
    -Actual @($releaseItems.Name) `
    -Description 'Release output'

$packagePath = Join-Path $directoryPath $packageName
$portablePath = Join-Path $directoryPath $portableName
$setupPath = Join-Path $directoryPath $setupName
$packageInfo = Get-Item -LiteralPath $packagePath
if ([long] $descriptor.packageSize -ne $packageInfo.Length -or
    (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash -cne $descriptor.packageSha256) {
    throw 'Descriptor size or SHA-256 does not match the full package.'
}

$expectedAssets = [Collections.Generic.Dictionary[string, string]]::new(
    [StringComparer]::Ordinal)
$expectedAssets.Add($setupName, 'Installer')
$expectedAssets.Add($packageName, 'Full')
$expectedAssets.Add($portableName, 'Portable')
$parsedAssetsDocument = Get-Content `
    -LiteralPath (Join-Path $directoryPath "assets.$ExpectedChannel.json") `
    -Raw | ConvertFrom-Json
$assetsDocument = @($parsedAssetsDocument)
if ($assetsDocument.Count -ne $expectedAssets.Count) {
    throw 'Velopack asset inventory has an unexpected number of entries.'
}
foreach ($asset in $assetsDocument) {
    Assert-ExactSet `
        -Expected @('RelativeFileName', 'Type') `
        -Actual @($asset.PSObject.Properties.Name) `
        -Description 'Velopack asset inventory entry'
    if (-not $expectedAssets.ContainsKey([string] $asset.RelativeFileName) -or
        $expectedAssets[[string] $asset.RelativeFileName] -cne [string] $asset.Type) {
        throw 'Velopack asset inventory contains an unexpected asset.'
    }
}

$releasesDocument = Get-Content `
    -LiteralPath (Join-Path $directoryPath "releases.$ExpectedChannel.json") `
    -Raw | ConvertFrom-Json
Assert-ExactSet `
    -Expected @('Assets') `
    -Actual @($releasesDocument.PSObject.Properties.Name) `
    -Description 'Velopack release feed'
$feedAssets = @($releasesDocument.Assets)
if ($feedAssets.Count -ne 1) {
    throw 'Velopack release feed must contain exactly one full package.'
}
$feed = $feedAssets[0]
Assert-ExactSet `
    -Expected @('PackageId', 'Version', 'Type', 'FileName', 'SHA1', 'SHA256', 'Size', 'NotesMarkdown', 'NotesHTML') `
    -Actual @($feed.PSObject.Properties.Name) `
    -Description 'Velopack release feed asset'
$packageSha1 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA1).Hash
if ($feed.PackageId -cne 'RobloxOne' -or
    $feed.Version -cne [string] $descriptor.version -or
    $feed.Type -cne 'Full' -or
    $feed.FileName -cne $packageName -or
    $feed.SHA1 -cne $packageSha1 -or
    $feed.SHA256 -cne [string] $descriptor.packageSha256 -or
    [long] $feed.Size -ne [long] $descriptor.packageSize -or
    (Get-NormalizedNotes ([string] $feed.NotesMarkdown)) -cne [string] $descriptor.releaseNotes) {
    throw 'Velopack release feed does not match the signed descriptor and package.'
}
if ($feed.NotesHTML.Length -gt 128 * 1024 -or
    $feed.NotesHTML -match '(?i)<script|javascript:' -or
    $feed.NotesHTML -match '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]') {
    throw 'Velopack rendered release notes are unsafe.'
}
$legacyFeed = (Get-Content -LiteralPath (Join-Path $directoryPath "RELEASES-$ExpectedChannel") -Raw).Trim()
if ($legacyFeed -cne "$packageSha1 $packageName $($packageInfo.Length)") {
    throw 'Legacy Velopack release metadata does not match the full package.'
}

$expectedPackageEntries = @(
    '[Content_Types].xml',
    '_rels/.rels',
    'RobloxOne.nuspec',
    'lib/app/LICENSE.md',
    'lib/app/RobloxOne.exe',
    'lib/app/RobloxOne_ExecutionStub.exe',
    'lib/app/Squirrel.exe',
    'lib/app/sq.version',
    'lib/app/THIRD_PARTY_NOTICES.md',
    'lib/app/licenses/DotNet-LICENSE.txt',
    'lib/app/licenses/DotNet-THIRD-PARTY-NOTICES.txt',
    'lib/app/licenses/Microsoft.Web.WebView2-LICENSE.txt',
    'lib/app/licenses/Microsoft.Web.WebView2-NOTICE.txt',
    'lib/app/licenses/Microsoft.WindowsDesktop-LICENSE.txt',
    'lib/app/licenses/Velopack-LICENSE.txt'
)
$packageExtraction = Join-Path ([IO.Path]::GetTempPath()) ("robloxone-package-" + [Guid]::NewGuid().ToString('N'))
try {
    $packageArchive = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        Assert-ExactSet `
            -Expected $expectedPackageEntries `
            -Actual @($packageArchive.Entries.FullName) `
            -Description 'Full package'
    }
    finally {
        $packageArchive.Dispose()
    }
    [IO.Compression.ZipFile]::ExtractToDirectory($packagePath, $packageExtraction)
    foreach ($relativePath in $sourceComparableApplicationFiles) {
        Assert-FileHashEqual `
            -Expected (Join-Path $applicationPath $relativePath) `
            -Actual (Join-Path $packageExtraction "lib/app/$relativePath") `
            -Description "Packaged $relativePath"
    }
    $packagedMainExecutable = Join-Path $packageExtraction 'lib/app/RobloxOne.exe'
    $packagedMainExecutableHash = (Get-FileHash `
        -LiteralPath $packagedMainExecutable `
        -Algorithm SHA256).Hash
    Assert-ExecutableVersion `
        -Path $packagedMainExecutable `
        -ExpectedVersion ([string] $descriptor.version)
    $nuspecPath = Join-Path $packageExtraction 'RobloxOne.nuspec'
    $versionMetadataPath = Join-Path $packageExtraction 'lib/app/sq.version'
    Assert-FileHashEqual `
        -Expected $nuspecPath `
        -Actual $versionMetadataPath `
        -Description 'Velopack version metadata'
    $versionMetadataHash = (Get-FileHash -LiteralPath $versionMetadataPath -Algorithm SHA256).Hash
    [xml] $nuspec = Get-Content -LiteralPath $nuspecPath -Raw
    $metadata = $nuspec.package.metadata
    if ((Get-XmlChildText $metadata 'id') -cne 'RobloxOne' -or
        (Get-XmlChildText $metadata 'version') -cne [string] $descriptor.version -or
        (Get-XmlChildText $metadata 'channel') -cne $ExpectedChannel -or
        (Get-XmlChildText $metadata 'mainExe') -cne 'RobloxOne.exe' -or
        (Get-XmlChildText $metadata 'rid') -cne 'win-x64' -or
        (Get-XmlChildText $metadata 'machineArchitecture') -cne 'x64' -or
        (Get-NormalizedNotes (Get-XmlChildText $metadata 'releaseNotes')) -cne [string] $descriptor.releaseNotes) {
        throw 'Velopack package metadata does not match the signed release.'
    }
    foreach ($relativePath in @(
            'lib/app/RobloxOne.exe',
            'lib/app/RobloxOne_ExecutionStub.exe',
            'lib/app/Squirrel.exe')) {
        Assert-Authenticode (Join-Path $packageExtraction $relativePath)
    }
}
finally {
    if (Test-Path -LiteralPath $packageExtraction) {
        Remove-Item -LiteralPath $packageExtraction -Recurse -Force
    }
}

$expectedPortableEntries = @(
    '.portable',
    'Roblox One.exe',
    'Update.exe',
    'current/LICENSE.md',
    'current/RobloxOne.exe',
    'current/sq.version',
    'current/THIRD_PARTY_NOTICES.md',
    'current/licenses/DotNet-LICENSE.txt',
    'current/licenses/DotNet-THIRD-PARTY-NOTICES.txt',
    'current/licenses/Microsoft.Web.WebView2-LICENSE.txt',
    'current/licenses/Microsoft.Web.WebView2-NOTICE.txt',
    'current/licenses/Microsoft.WindowsDesktop-LICENSE.txt',
    'current/licenses/Velopack-LICENSE.txt'
)
$portableExtraction = Join-Path ([IO.Path]::GetTempPath()) ("robloxone-portable-" + [Guid]::NewGuid().ToString('N'))
try {
    $portableArchive = [IO.Compression.ZipFile]::OpenRead($portablePath)
    try {
        Assert-ExactSet `
            -Expected $expectedPortableEntries `
            -Actual @($portableArchive.Entries.FullName) `
            -Description 'Portable ZIP'
    }
    finally {
        $portableArchive.Dispose()
    }
    [IO.Compression.ZipFile]::ExtractToDirectory($portablePath, $portableExtraction)
    foreach ($relativePath in $sourceComparableApplicationFiles) {
        Assert-FileHashEqual `
            -Expected (Join-Path $applicationPath $relativePath) `
            -Actual (Join-Path $portableExtraction "current/$relativePath") `
            -Description "Portable $relativePath"
    }
    $portableMainExecutable = Join-Path $portableExtraction 'current/RobloxOne.exe'
    $portableMainExecutableHash = (Get-FileHash `
        -LiteralPath $portableMainExecutable `
        -Algorithm SHA256).Hash
    if ($portableMainExecutableHash -cne $packagedMainExecutableHash) {
        throw 'Portable RobloxOne.exe does not match the signed full package.'
    }
    Assert-ExecutableVersion `
        -Path $portableMainExecutable `
        -ExpectedVersion ([string] $descriptor.version)
    $portableVersionHash = (Get-FileHash `
        -LiteralPath (Join-Path $portableExtraction 'current/sq.version') `
        -Algorithm SHA256).Hash
    if ($portableVersionHash -cne $versionMetadataHash) {
        throw 'Portable version metadata does not match the full package.'
    }
    foreach ($relativePath in @('current/RobloxOne.exe', 'Roblox One.exe', 'Update.exe')) {
        Assert-Authenticode (Join-Path $portableExtraction $relativePath)
    }
}
finally {
    if (Test-Path -LiteralPath $portableExtraction) {
        Remove-Item -LiteralPath $portableExtraction -Recurse -Force
    }
}
Assert-Authenticode $setupPath

$sbomPath = Join-Path $directoryPath $sbomName
$sbomInfo = Get-Item -LiteralPath $sbomPath
if ($sbomInfo.Length -le 0 -or $sbomInfo.Length -gt 2 * 1024 * 1024) {
    throw 'Release SBOM must be between 1 byte and 2 MiB.'
}
$sbomText = Get-Content -LiteralPath $sbomPath -Raw
$windowsUsersSegment = '\Use' + 'rs\'
$unixHomeSegment = '/' + 'home/'
$unixUsersSegment = '/' + 'Users/'
$machinePathPattern = '(?i)([A-Z]:' + [regex]::Escape($windowsUsersSegment) +
    '|' + [regex]::Escape($unixHomeSegment) + '[^/]+/' +
    '|' + [regex]::Escape($unixUsersSegment) + '[^/]+/)'
if ($sbomText -match $machinePathPattern) {
    throw 'Release SBOM contains a machine-specific user path.'
}
$sbom = $sbomText | ConvertFrom-Json
if ($sbom.spdxVersion -cne 'SPDX-2.3' -or
    $sbom.dataLicense -cne 'CC0-1.0' -or
    $sbom.SPDXID -cne 'SPDXRef-DOCUMENT' -or
    $sbom.name -cne "RobloxOne-$($descriptor.version)-win-x64" -or
    $sbom.documentNamespace -cne "https://spdx.org/spdxdocs/RobloxOne-$($descriptor.version)-$($descriptor.packageSha256.ToLowerInvariant())") {
    throw 'Release SBOM identity does not match the signed release.'
}
$sbomPackage = @($sbom.packages | Where-Object { $_.SPDXID -ceq 'SPDXRef-Package-RobloxOne' })
if ($sbomPackage.Count -ne 1 -or
    $sbomPackage[0].name -cne $packageName -or
    $sbomPackage[0].versionInfo -cne [string] $descriptor.version) {
    throw 'Release SBOM does not describe the full release package.'
}
$sbomChecksum = @($sbomPackage[0].checksums | Where-Object { $_.algorithm -ceq 'SHA256' })
if ($sbomChecksum.Count -ne 1 -or
    $sbomChecksum[0].checksumValue -cne [string] $descriptor.packageSha256) {
    throw 'Release SBOM package checksum does not match the descriptor.'
}
$licenseInfo = @($sbom.hasExtractedLicensingInfos |
    Where-Object { $_.licenseId -ceq 'LicenseRef-RobloxOne-Release-License' })
if ($licenseInfo.Count -ne 1 -or
    (Get-NormalizedNotes ([string] $licenseInfo[0].extractedText)) -cne
        (Get-NormalizedNotes (Get-Content -LiteralPath (Join-Path $applicationPath 'LICENSE.md') -Raw))) {
    throw 'Release SBOM does not contain the approved release license.'
}
$requiredSbomPackages = @(
    'Microsoft.NETCore.App.Runtime.win-x64',
    'Microsoft.Web.WebView2',
    'Microsoft.WindowsDesktop.App.Runtime.win-x64',
    'Velopack'
)
foreach ($requiredPackage in $requiredSbomPackages) {
    if (@($sbom.packages | Where-Object { $_.name -ceq $requiredPackage }).Count -ne 1) {
        throw "Release SBOM is missing required component '$requiredPackage'."
    }
}

$checksumPath = Join-Path $directoryPath 'SHA256SUMS.txt'
$checksumLines = @(Get-Content -LiteralPath $checksumPath)
$assetsWithoutChecksum = @($expectedReleaseFiles | Where-Object { $_ -cne 'SHA256SUMS.txt' } | Sort-Object)
if ($checksumLines.Count -ne $assetsWithoutChecksum.Count) {
    throw 'SHA256SUMS.txt does not cover every release asset exactly once.'
}
$checksumNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($line in $checksumLines) {
    if ($line -cnotmatch '^([0-9a-f]{64})  ([A-Za-z0-9][A-Za-z0-9._-]*)$') {
        throw 'SHA256SUMS.txt contains a malformed line.'
    }
    $hash = $Matches[1]
    $name = $Matches[2]
    if (-not $checksumNames.Add($name) -or $name -ceq 'SHA256SUMS.txt') {
        throw 'SHA256SUMS.txt contains a duplicate or self-referential entry.'
    }
    $actualHash = (Get-FileHash -LiteralPath (Join-Path $directoryPath $name) -Algorithm SHA256).
        Hash.ToLowerInvariant()
    if ($actualHash -cne $hash) {
        throw "SHA256SUMS.txt does not match release asset '$name'."
    }
}
Assert-ExactSet `
    -Expected $assetsWithoutChecksum `
    -Actual @($checksumNames) `
    -Description 'SHA256SUMS.txt'

Write-Host 'Verified exact release inventory, feeds, SPDX SBOM, checksums, licenses, package contents, and every executable signature.'
