[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Descriptor,

    [Parameter(Mandatory)]
    [string] $Project,

    [Parameter(Mandatory)]
    [string] $LockFile,

    [Parameter(Mandatory)]
    [string] $License,

    [Parameter(Mandatory)]
    [string] $Output
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Require-File([string] $Path, [string] $Description) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "$Description not found: $fullPath"
    }
    return $fullPath
}

function New-SpdxPackage(
    [string] $Name,
    [string] $Version,
    [string] $LicenseId,
    [string] $Supplier,
    [string] $SpdxId) {
    return [ordered]@{
        name = $Name
        SPDXID = $SpdxId
        versionInfo = $Version
        downloadLocation = 'NOASSERTION'
        filesAnalyzed = $false
        licenseConcluded = 'NOASSERTION'
        licenseDeclared = $LicenseId
        copyrightText = 'NOASSERTION'
        supplier = $Supplier
        externalRefs = @(
            [ordered]@{
                referenceCategory = 'PACKAGE-MANAGER'
                referenceType = 'purl'
                referenceLocator = "pkg:nuget/$Name@$Version"
            }
        )
    }
}

$descriptorPath = Require-File $Descriptor 'Release descriptor'
$projectPath = Require-File $Project 'Application project'
$lockPath = Require-File $LockFile 'Application package lock'
$licensePath = Require-File $License 'Release license'
$outputPath = [IO.Path]::GetFullPath($Output)
$strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
$licenseText = [IO.File]::ReadAllText($licensePath, $strictUtf8)

$release = Get-Content -LiteralPath $descriptorPath -Raw | ConvertFrom-Json
Write-Verbose 'Parsed release descriptor.'
if ($release.version -cnotmatch '^\d+\.\d+\.\d+$' -or
    $release.tag -cne "v$($release.version)" -or
    $release.repository -cne 'Makmatoe/RobloxOne' -or
    $release.packageSha256 -cnotmatch '^[0-9A-F]{64}$') {
    throw 'The release descriptor is not valid SBOM input.'
}
if ([IO.Path]::GetFileName($outputPath) -cne "RobloxOne-$($release.version)-sbom.spdx.json") {
    throw 'The SPDX SBOM filename must contain the exact release version.'
}

[xml] $projectXml = Get-Content -LiteralPath $projectPath -Raw
$runtimeVersions = @($projectXml.SelectNodes('/Project/PropertyGroup/RuntimeFrameworkVersion') |
    ForEach-Object { $_.InnerText } | Where-Object { $_ })
if ($runtimeVersions.Count -ne 1 -or $runtimeVersions[0] -cnotmatch '^\d+\.\d+\.\d+$') {
    throw 'The project must pin exactly one three-part RuntimeFrameworkVersion.'
}
$runtimeVersion = [string] $runtimeVersions[0]

$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
Write-Verbose 'Parsed application lock file.'
$resolved = @{}
foreach ($framework in $lock.dependencies.PSObject.Properties) {
    foreach ($dependency in $framework.Value.PSObject.Properties) {
        $value = $dependency.Value
        if ($value.type -cne 'Direct' -or
            $dependency.Name -ceq 'Microsoft.NET.ILLink.Tasks') {
            continue
        }
        $id = [string] $dependency.Name
        $version = [string] $value.resolved
        if ($version -cnotmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
            throw "Dependency '$id' has an unsupported resolved version '$version'."
        }
        if ($resolved.ContainsKey($id) -and $resolved[$id] -cne $version) {
            throw "Dependency '$id' resolves to more than one version."
        }
        $resolved[$id] = $version
    }
}

$licenses = @{
    'Microsoft.Web.WebView2' = 'BSD-3-Clause'
    'Velopack' = 'MIT'
}
$suppliers = @{
    'Microsoft.Web.WebView2' = 'Organization: Microsoft Corporation'
    'Velopack' = 'Organization: Velopack Ltd'
}
$packages = [Collections.Generic.List[object]]::new()
$releasePackage = [ordered]@{
    name = [string] $release.packageFile
    SPDXID = 'SPDXRef-Package-RobloxOne'
    versionInfo = [string] $release.version
    downloadLocation = "https://github.com/Makmatoe/RobloxOne/releases/download/$($release.tag)/$($release.packageFile)"
    filesAnalyzed = $false
    checksums = @(
        [ordered]@{
            algorithm = 'SHA256'
            checksumValue = [string] $release.packageSha256
        }
    )
    licenseConcluded = 'NOASSERTION'
    licenseDeclared = 'LicenseRef-RobloxOne-Release-License'
    copyrightText = 'Copyright (c) 2026 Makmatoe'
    supplier = 'Person: Makmatoe'
}
$packages.Add($releasePackage)

foreach ($dependency in $resolved.GetEnumerator() | Sort-Object Key) {
    $name = [string] $dependency.Key
    if (-not $licenses.ContainsKey($name) -or -not $suppliers.ContainsKey($name)) {
        throw "Dependency '$name' is missing an explicit SBOM license or supplier mapping."
    }
    $id = 'SPDXRef-Package-' + ($name -replace '[^A-Za-z0-9.-]', '-')
    $packages.Add((New-SpdxPackage `
        -Name $name `
        -Version ([string] $dependency.Value) `
        -LicenseId ([string] $licenses[$name]) `
        -Supplier ([string] $suppliers[$name]) `
        -SpdxId $id))
}
foreach ($runtimeName in @(
        'Microsoft.NETCore.App.Runtime.win-x64',
        'Microsoft.WindowsDesktop.App.Runtime.win-x64')) {
    $id = 'SPDXRef-Package-' + ($runtimeName -replace '[^A-Za-z0-9.-]', '-')
    $packages.Add((New-SpdxPackage `
        -Name $runtimeName `
        -Version $runtimeVersion `
        -LicenseId 'MIT' `
        -Supplier 'Organization: Microsoft Corporation' `
        -SpdxId $id))
}
Write-Verbose 'Constructed SPDX package list.'

$relationships = [Collections.Generic.List[object]]::new()
$relationships.Add([ordered]@{
    spdxElementId = 'SPDXRef-DOCUMENT'
    relationshipType = 'DESCRIBES'
    relatedSpdxElement = 'SPDXRef-Package-RobloxOne'
})
foreach ($package in @($packages.ToArray() | Where-Object { $_.SPDXID -cne 'SPDXRef-Package-RobloxOne' })) {
    $relationships.Add([ordered]@{
        spdxElementId = 'SPDXRef-Package-RobloxOne'
        relationshipType = 'DEPENDS_ON'
        relatedSpdxElement = $package.SPDXID
    })
}
Write-Verbose 'Constructed SPDX relationships.'

$publishedAt = [DateTimeOffset]::ParseExact(
    [string] $release.publishedAt,
    'O',
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
$document = [ordered]@{
    spdxVersion = 'SPDX-2.3'
    dataLicense = 'CC0-1.0'
    SPDXID = 'SPDXRef-DOCUMENT'
    name = "RobloxOne-$($release.version)-win-x64"
    documentNamespace = "https://spdx.org/spdxdocs/RobloxOne-$($release.version)-$($release.packageSha256.ToLowerInvariant())"
    creationInfo = [ordered]@{
        created = $publishedAt.ToString('yyyy-MM-ddTHH:mm:ssZ', [Globalization.CultureInfo]::InvariantCulture)
        creators = @('Tool: RobloxOne-New-ReleaseSbom.ps1')
        licenseListVersion = '3.26'
    }
    packages = $packages.ToArray()
    relationships = $relationships.ToArray()
    hasExtractedLicensingInfos = @(
        [ordered]@{
            licenseId = 'LicenseRef-RobloxOne-Release-License'
            extractedText = $licenseText
            name = 'Roblox One approved release license'
        }
    )
}
Write-Verbose 'Constructed SPDX document.'

$outputDirectory = Split-Path -Parent $outputPath
if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
    throw "SBOM output directory not found: $outputDirectory"
}
$json = $document | ConvertTo-Json -Depth 6
Write-Verbose 'Serialized SPDX document.'
[IO.File]::WriteAllText(
    $outputPath,
    $json + "`n",
    [Text.UTF8Encoding]::new($false))
Write-Host "Wrote SPDX 2.3 SBOM for Roblox One $($release.version)."
