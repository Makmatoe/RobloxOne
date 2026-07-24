[CmdletBinding()]
param(
    [string] $OutputDirectory = 'artifacts/runtime-smoke',

    [ValidateRange(1, 120)]
    [int] $TimeoutSeconds = 30
)

. (Join-Path $PSScriptRoot 'Common.ps1')

$root = Get-RepositoryRoot
$project = Get-ApplicationProject
$output = Assert-SafeOutputDirectory (Join-Path $root $OutputDirectory)

Push-Location $root
try {
    if (Test-Path -LiteralPath $output) {
        Remove-SafeOutputDirectory $output
    }
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    Invoke-CheckedCommand dotnet restore $project '--locked-mode' '--runtime' 'win-x64'
    Invoke-CheckedCommand dotnet publish $project `
        '--configuration' 'Release' `
        '--runtime' 'win-x64' `
        '--self-contained' 'true' `
        '--no-restore' `
        '--output' $output `
        '-p:EnableRuntimeSmokeHarness=true' `
        '-p:ContinuousIntegrationBuild=true' `
        '-p:Deterministic=true'
    & (Join-Path $PSScriptRoot 'Test-RuntimeSmoke.ps1') `
        -ApplicationPath (Join-Path $output 'SessionDock.exe') `
        -TimeoutSeconds $TimeoutSeconds
}
finally {
    Pop-Location
}
