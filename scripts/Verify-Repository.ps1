[CmdletBinding()]
param(
    [switch] $CI
)

. (Join-Path $PSScriptRoot 'Common.ps1')

$root = Get-RepositoryRoot
Push-Location $root
try {
    $requiredFiles = @(
        '.config/dotnet-tools.json',
        'Directory.Build.props',
        'global.json',
        'NuGet.Config',
        'RobloxOneLauncher/RobloxOneLauncher.csproj',
        'RobloxOneLauncher/Resources/update-public-key.pem'
    )
    foreach ($relativePath in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $root $relativePath) -PathType Leaf)) {
            throw "Required repository file is missing: $relativePath"
        }
    }

    $expectedSdk = (Get-Content -LiteralPath (Join-Path $root 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $actualSdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $expectedSdk) {
        throw "The repository requires .NET SDK $expectedSdk; dotnet selected '$actualSdk'."
    }

    $toolManifest = Get-Content -LiteralPath (Join-Path $root '.config/dotnet-tools.json') -Raw | ConvertFrom-Json
    if ($toolManifest.tools.vpk.version -ne '1.2.0' -or $toolManifest.tools.vpk.rollForward -ne $false) {
        throw 'The local vpk tool must remain pinned exactly to version 1.2.0 with roll-forward disabled.'
    }

    [void] (Get-ProjectVersion)

    $trackedFiles = @(& git ls-files)
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to inspect tracked repository files.'
    }

    $forbiddenPatterns = @(
        '(^|/)(bin|obj|artifacts|publish|Releases|TestResults)/',
        '(^|/)\.env($|\.)',
        '(?i)(private|secret|credential)[^/]*\.(pem|key|pfx|p12|jks|keystore)$',
        '(?i)update-private-key\.pem$',
        '(?i)\.(robloxone-update|nupkg|snupkg)$'
    )
    foreach ($file in $trackedFiles) {
        foreach ($pattern in $forbiddenPatterns) {
            if ($file -match $pattern) {
                throw "Generated output or sensitive material must not be tracked: $file"
            }
        }
    }

    if ($trackedFiles.Count -gt 0) {
        $secretContentPattern =
            'BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY|gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|\.ROBLOSECURITY|AKIA[0-9A-Z]{16}|xox[baprs]-'
        & git grep -I -q -E $secretContentPattern -- . `
            ':(exclude)scripts/Verify-Repository.ps1'
        $secretScanExitCode = $LASTEXITCODE
        if ($secretScanExitCode -eq 0) {
            throw 'A tracked file matches a prohibited credential or private-key pattern.'
        }
        if ($secretScanExitCode -ne 1) {
            throw "Tracked-content secret scan failed with exit code $secretScanExitCode."
        }
        $global:LASTEXITCODE = 0
    }

    $workflowDirectory = Join-Path $root '.github/workflows'
    if (Test-Path -LiteralPath $workflowDirectory -PathType Container) {
        $mutableActionRef = '(?m)^\s*-?\s*uses:\s*[^#\r\n]+@(?![0-9a-f]{40}(?:\s|#|$))'
        $exactSdkPattern = '(?m)^\s+dotnet-version:\s*[''"]?{0}[''"]?\s*$' -f
            [regex]::Escape($expectedSdk)
        foreach ($workflow in Get-ChildItem -LiteralPath $workflowDirectory -File -Include '*.yml', '*.yaml') {
            $contents = Get-Content -LiteralPath $workflow.FullName -Raw
            if ($contents -match $mutableActionRef) {
                throw "Workflow action references must use full commit SHAs: $($workflow.Name)"
            }
            if ($contents -match '(?m)^\s*pull_request_target\s*:') {
                throw "pull_request_target is intentionally prohibited: $($workflow.Name)"
            }
            if ($contents -match 'actions/setup-dotnet@' -and
                $contents -notmatch $exactSdkPattern) {
                throw "Workflow must install the exact repository SDK ${expectedSdk}: $($workflow.Name)"
            }
            if ($contents -match '(?m)^\s+global-json-file:') {
                throw "Workflow must use an exact dotnet-version, not setup-dotnet's feature-band global-json behavior: $($workflow.Name)"
            }
        }
    }

    if ($CI) {
        $projects = @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.csproj' |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })
        foreach ($project in $projects) {
            $lockFile = Join-Path $project.DirectoryName 'packages.lock.json'
            if (-not (Test-Path -LiteralPath $lockFile -PathType Leaf)) {
                throw "CI requires a committed lock file beside every project: $lockFile"
            }
        }
    }

    Write-Host "Repository validation passed for Roblox One $(Get-ProjectVersion)."
}
finally {
    Pop-Location
}
