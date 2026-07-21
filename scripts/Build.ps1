[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [ValidatePattern('^win-(x64|arm64)$')]
    [string] $Runtime = 'win-x64',

    [string] $OutputDirectory = 'artifacts/publish',

    [switch] $CI,

    [switch] $SkipPublish
)

. (Join-Path $PSScriptRoot 'Common.ps1')

$root = Get-RepositoryRoot
$project = Get-ApplicationProject
$output = Assert-SafeOutputDirectory (Join-Path $root $OutputDirectory)
$commonProperties = @(
    "-p:ContinuousIntegrationBuild=$($CI.IsPresent.ToString().ToLowerInvariant())",
    '-p:Deterministic=true'
)

Push-Location $root
try {
    & (Join-Path $PSScriptRoot 'Verify-Repository.ps1') -CI:$CI

    $projects = [Collections.Generic.List[string]]::new()
    $projects.Add($project)
    foreach ($testProject in Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*Tests.csproj' |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        Sort-Object FullName) {
        $projects.Add($testProject.FullName)
    }

    $signerProject = Join-Path $root 'RobloxOneLauncher/tools/ReleaseSigner/ReleaseSigner.csproj'
    if (Test-Path -LiteralPath $signerProject -PathType Leaf) {
        $projects.Add($signerProject)
    }

    foreach ($item in $projects | Select-Object -Unique) {
        $restoreArguments = @('restore', $item, '--locked-mode', '--runtime', $Runtime)
        Invoke-CheckedCommand dotnet @restoreArguments
    }

    if ($CI) {
        & (Join-Path $PSScriptRoot 'Verify-NuGetSecurity.ps1') `
            -Project (Join-Path $root 'RobloxOne.slnx')
    }

    Invoke-CheckedCommand dotnet build $project '--configuration' $Configuration '--runtime' $Runtime '--no-restore' @commonProperties

    if (Test-Path -LiteralPath $signerProject -PathType Leaf) {
        Invoke-CheckedCommand dotnet build $signerProject '--configuration' $Configuration '--runtime' $Runtime '--no-restore' @commonProperties
    }

    foreach ($testProject in $projects | Where-Object { $_ -like '*Tests.csproj' }) {
        Invoke-CheckedCommand dotnet build $testProject '--configuration' $Configuration '--runtime' $Runtime '--no-restore' @commonProperties
        Invoke-CheckedCommand dotnet test $testProject '--configuration' $Configuration '--runtime' $Runtime `
            '--no-restore' '--no-build' @commonProperties
    }

    if (-not $SkipPublish) {
        if (Test-Path -LiteralPath $output) {
            Remove-Item -LiteralPath $output -Recurse -Force
        }
        New-Item -ItemType Directory -Path $output -Force | Out-Null
        Invoke-CheckedCommand dotnet publish $project '--configuration' $Configuration '--runtime' $Runtime `
            '--self-contained' 'true' '--no-restore' '--output' $output @commonProperties
        if (-not (Test-Path -LiteralPath (Join-Path $output 'RobloxOne.exe') -PathType Leaf)) {
            throw "Publish completed without the expected RobloxOne.exe in $output."
        }
    }
}
finally {
    Pop-Location
}
