[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v\d+\.\d+\.\d+$')]
    [string] $Tag,

    [Parameter(Mandatory)]
    [string] $AzureTrustedSignFile,

    [Parameter(Mandatory)]
    [string] $ExpectedPublisherSubject,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-F]{64}$')]
    [string] $ApprovedReleaseLicenseSha256,

    [string] $OutputDirectory = 'artifacts/release'
)

. (Join-Path $PSScriptRoot 'Common.ps1')

$root = Get-RepositoryRoot
$project = Get-ApplicationProject
$output = Assert-SafeOutputDirectory (Join-Path $root $OutputDirectory)
$appOutput = Assert-SafeOutputDirectory (Join-Path $root 'artifacts/publish')
if ($output.Equals($appOutput, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Release output must be different from the application publish directory.'
}
$signingFile = [IO.Path]::GetFullPath($AzureTrustedSignFile)
if (-not (Test-Path -LiteralPath $signingFile -PathType Leaf)) {
    throw "Azure Artifact Signing metadata file not found: $signingFile"
}
if ($signingFile.StartsWith($output.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Azure signing metadata must be stored outside the release output directory.'
}
if ([string]::IsNullOrWhiteSpace($env:UPDATE_SIGNING_PRIVATE_KEY_PEM)) {
    throw 'UPDATE_SIGNING_PRIVATE_KEY_PEM is required. Roblox One never creates an unsigned release.'
}

Push-Location $root
try {
    & (Join-Path $PSScriptRoot 'Verify-Release.ps1') `
        -Tag $Tag `
        -RequireTagAtHead `
        -RequireMainAtHead `
        -RequireAnnotatedTag `
        -RequireCleanWorkingTree
    & (Join-Path $PSScriptRoot 'Build.ps1') -Configuration Release -Runtime win-x64 -OutputDirectory 'artifacts/publish' -CI
    Invoke-CheckedCommand dotnet tool restore

    $version = Get-ProjectVersion
    if (Test-Path -LiteralPath $output) {
        Remove-SafeOutputDirectory $output
    }
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    Invoke-CheckedCommand dotnet tool run vpk -- pack `
        '--packId' 'RobloxOne' `
        '--packVersion' $version `
        '--packDir' $appOutput `
        '--mainExe' 'RobloxOne.exe' `
        '--packTitle' 'Roblox One' `
        '--packAuthors' 'Makmatoe' `
        '--releaseNotes' (Join-Path $root "RobloxOneLauncher/ReleaseNotes/$version.md") `
        '--runtime' 'win-x64' `
        '--channel' 'win-x64-stable' `
        '--outputDir' $output `
        '--azureTrustedSignFile' $signingFile

    $fullPackages = @(Get-ChildItem -LiteralPath $output -File -Filter '*-full.nupkg')
    if ($fullPackages.Count -ne 1) {
        throw "Expected exactly one full Velopack package; found $($fullPackages.Count)."
    }

    $signerProject = Join-Path $root 'RobloxOneLauncher/tools/ReleaseSigner/ReleaseSigner.csproj'
    $notesPath = Join-Path $root "RobloxOneLauncher/ReleaseNotes/$version.md"
    $descriptorPath = Join-Path $output 'robloxone-release.json'
    Invoke-CheckedCommand dotnet run '--project' $signerProject '--configuration' 'Release' '--runtime' 'win-x64' `
        '--no-restore' '--' `
        'sign' '--package' $fullPackages[0].FullName '--notes' $notesPath '--output' $descriptorPath `
        '--repository' 'Makmatoe/RobloxOne' '--channel' 'win-x64-stable' '--version' $version `
        '--tag' $Tag '--private-key-env' 'UPDATE_SIGNING_PRIVATE_KEY_PEM'

    $publicKeyPath = Join-Path $root 'RobloxOneLauncher/Resources/update-public-key.pem'
    Invoke-CheckedCommand dotnet run '--project' $signerProject '--configuration' 'Release' '--runtime' 'win-x64' `
        '--no-restore' '--' `
        'verify' '--manifest' $descriptorPath '--package' $fullPackages[0].FullName '--public-key' $publicKeyPath

    $sbomPath = Join-Path $output "RobloxOne-$version-sbom.spdx.json"
    & (Join-Path $PSScriptRoot 'New-ReleaseSbom.ps1') `
        -Descriptor $descriptorPath `
        -Project $project `
        -LockFile (Join-Path $root 'RobloxOneLauncher/packages.lock.json') `
        -License (Join-Path $appOutput 'LICENSE.md') `
        -Output $sbomPath
    & (Join-Path $PSScriptRoot 'New-ReleaseChecksums.ps1') -Directory $output

    & (Join-Path $PSScriptRoot 'Verify-Assets.ps1') -Directory $output -Manifest $descriptorPath `
        -PublishedApplicationDirectory $appOutput `
        -ExpectedPublisherSubject $ExpectedPublisherSubject `
        -ApprovedReleaseLicenseSha256 $ApprovedReleaseLicenseSha256 `
        -ExpectedTag $Tag
}
finally {
    Pop-Location
}
