Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RepositoryRoot {
    return (Split-Path -Parent $PSScriptRoot)
}

function Get-ApplicationProject {
    return (Join-Path (Get-RepositoryRoot) 'RobloxOneLauncher/RobloxOneLauncher.csproj')
}

function Get-ProjectVersion {
    $projectPath = Get-ApplicationProject
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "Application project not found: $projectPath"
    }

    [xml] $project = Get-Content -LiteralPath $projectPath -Raw
    $versions = @($project.SelectNodes('/Project/PropertyGroup/Version') |
        ForEach-Object { $_.InnerText } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($versions.Count -ne 1) {
        throw 'The application project must declare exactly one non-empty <Version> value.'
    }

    $version = [string] $versions[0]
    if ($version -cnotmatch '^\d+\.\d+\.\d+$') {
        throw "Project version '$version' must use stable major.minor.patch format."
    }

    return $version
}

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)]
        [string] $Command,

        [Parameter(ValueFromRemainingArguments)]
        [string[]] $Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $Command $($Arguments -join ' ')"
    }
}

function Assert-SafeOutputDirectory {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $root = [IO.Path]::GetFullPath((Get-RepositoryRoot)).TrimEnd('\', '/')
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $artifactsRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts')).TrimEnd('\', '/')
    if (-not $fullPath.StartsWith("$artifactsRoot$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Output directory must be a child directory of $artifactsRoot. Received: $fullPath"
    }

    return $fullPath
}
