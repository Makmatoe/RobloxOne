[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Directory
)

. (Join-Path $PSScriptRoot 'Common.ps1')

$root = Get-RepositoryRoot
$directoryPath = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $directoryPath -PathType Container)) {
    throw "Publish directory not found: $directoryPath"
}

$expectedFiles = @(
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
$items = @(Get-ChildItem -LiteralPath $directoryPath -Recurse -Force)
if ($items | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string] $_.LinkType)
    }) {
    throw 'Publish output must not contain symbolic links, junctions, or other reparse points.'
}

$actualFiles = @($items | Where-Object { -not $_.PSIsContainer } | ForEach-Object {
    $_.FullName.Substring($directoryPath.Length + 1).Replace('\', '/')
} | Sort-Object)
$differences = @(Compare-Object -ReferenceObject $expectedFiles -DifferenceObject $actualFiles -CaseSensitive)
if ($differences.Count -ne 0 -or $actualFiles.Count -ne $expectedFiles.Count) {
    throw "Publish output contains missing or unexpected files:`n$($differences | Out-String)"
}

$expectedDirectories = @('licenses')
$actualDirectories = @($items | Where-Object { $_.PSIsContainer } | ForEach-Object {
    $_.FullName.Substring($directoryPath.Length + 1).Replace('\', '/')
} | Sort-Object)
$directoryDifferences = @(Compare-Object `
    -ReferenceObject $expectedDirectories `
    -DifferenceObject $actualDirectories `
    -CaseSensitive)
if ($directoryDifferences.Count -ne 0 -or
    $actualDirectories.Count -ne $expectedDirectories.Count) {
    throw 'Publish output contains missing or unexpected directories.'
}

$applicationPath = Join-Path $directoryPath 'RobloxOne.exe'
$application = Get-Item -LiteralPath $applicationPath
if ($application.Length -lt 1024 * 1024 -or $application.Length -gt 1024L * 1024 * 1024) {
    throw 'Published RobloxOne.exe has an invalid size.'
}
$version = Get-ProjectVersion
$fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($applicationPath).FileVersion
$parsedFileVersion = $null
if (-not [Version]::TryParse($fileVersion, [ref] $parsedFileVersion) -or
    $parsedFileVersion.ToString(3) -cne $version) {
    throw "Published RobloxOne.exe version '$fileVersion' does not match project version '$version'."
}

$assetsPath = Join-Path $root 'RobloxOneLauncher/obj/project.assets.json'
if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
    throw 'Restore assets are unavailable; publish notices cannot be verified.'
}
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
$packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
if ($packageRoots.Count -ne 1) {
    throw 'Expected exactly one NuGet global-packages directory.'
}
$packageRoot = [string] $packageRoots[0]

$sources = [ordered]@{
    'LICENSE.md' = Join-Path $root 'LICENSE.md'
    'THIRD_PARTY_NOTICES.md' = Join-Path $root 'THIRD_PARTY_NOTICES.md'
    'licenses/Velopack-LICENSE.txt' = Join-Path $root 'licenses/Velopack-LICENSE.txt'
    'licenses/DotNet-LICENSE.txt' = Join-Path $packageRoot 'microsoft.netcore.app.runtime.win-x64/10.0.8/LICENSE.TXT'
    'licenses/DotNet-THIRD-PARTY-NOTICES.txt' = Join-Path $packageRoot 'microsoft.netcore.app.runtime.win-x64/10.0.8/THIRD-PARTY-NOTICES.TXT'
    'licenses/Microsoft.WindowsDesktop-LICENSE.txt' = Join-Path $packageRoot 'microsoft.windowsdesktop.app.runtime.win-x64/10.0.8/LICENSE'
    'licenses/Microsoft.Web.WebView2-LICENSE.txt' = Join-Path $packageRoot 'microsoft.web.webview2/1.0.4078.44/LICENSE.txt'
    'licenses/Microsoft.Web.WebView2-NOTICE.txt' = Join-Path $packageRoot 'microsoft.web.webview2/1.0.4078.44/NOTICE.txt'
}
foreach ($entry in $sources.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath $entry.Value -PathType Leaf)) {
        throw "Required release notice source is unavailable: $($entry.Value)"
    }
    $publishedPath = Join-Path $directoryPath $entry.Key
    $sourceHash = (Get-FileHash -LiteralPath $entry.Value -Algorithm SHA256).Hash
    $publishedHash = (Get-FileHash -LiteralPath $publishedPath -Algorithm SHA256).Hash
    if ($sourceHash -cne $publishedHash) {
        throw "Published notice '$($entry.Key)' does not match its pinned source."
    }
}

Write-Host "Verified exact publish inventory, version, and complete pinned notices for Roblox One $version."
