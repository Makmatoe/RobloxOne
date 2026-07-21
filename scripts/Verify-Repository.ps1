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
        'LICENSE.md',
        'THIRD_PARTY_NOTICES.md',
        'RobloxOneLauncher/RobloxOneLauncher.csproj',
        'RobloxOneLauncher/Resources/update-public-key.pem',
        'licenses/Velopack-LICENSE.txt',
        'scripts/New-ReleaseChecksums.ps1',
        'scripts/New-ReleaseSbom.ps1',
        'scripts/Enable-HandleScope.ps1',
        'scripts/Verify-Assets.ps1',
        'scripts/Verify-Publish.ps1',
        'scripts/Verify-ReleaseLicense.ps1'
    )
    foreach ($relativePath in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $root $relativePath) -PathType Leaf)) {
            throw "Required repository file is missing: $relativePath"
        }
    }
    $velopackLicenseHash = (Get-FileHash `
        -LiteralPath (Join-Path $root 'licenses/Velopack-LICENSE.txt') `
        -Algorithm SHA256).Hash
    if ($velopackLicenseHash -cne '91845DB83551C877EBBB1118E0FB92E4E527290D23B995C55DCD438B3293943F') {
        throw 'The bundled Velopack license must match the pinned 1.2.0 upstream license exactly.'
    }
    & (Join-Path $PSScriptRoot 'Verify-ReleaseLicense.ps1') `
        -LicensePath (Join-Path $root 'LICENSE.md')

    $globalJson = Get-Content -LiteralPath (Join-Path $root 'global.json') -Raw |
        ConvertFrom-Json
    $expectedSdk = $globalJson.sdk.version
    if ($globalJson.sdk.rollForward -cne 'disable' -or
        $globalJson.sdk.allowPrerelease -ne $false) {
        throw 'global.json must disable SDK roll-forward and prerelease selection.'
    }
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
        '(?i)(^|/)(id_rsa|id_ed25519)(\.|$)',
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
            'BEGIN (RSA |EC |OPENSSH |ENCRYPTED )?PRIVATE KEY|gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|\.ROBLOSECURITY|A(KIA|SIA)[0-9A-Z]{16}|xox[baprs]-|AIza[0-9A-Za-z_-]{30,}'
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

        $machinePathPattern = '([A-Za-z]:\\Users\\[^\\/[:space:]]+|/home/[^/[:space:]]+|/Users/[^/[:space:]]+)'
        & git grep -I -q -E $machinePathPattern -- . `
            ':(exclude)scripts/Verify-Repository.ps1'
        $pathScanExitCode = $LASTEXITCODE
        if ($pathScanExitCode -eq 0) {
            throw 'A tracked file contains a machine-specific user path.'
        }
        if ($pathScanExitCode -ne 1) {
            throw "Tracked-content path scan failed with exit code $pathScanExitCode."
        }
        $global:LASTEXITCODE = 0
    }

    $projects = @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.csproj' |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })
    foreach ($projectFile in $projects) {
        [xml] $projectXml = Get-Content -LiteralPath $projectFile.FullName -Raw
        foreach ($reference in @($projectXml.SelectNodes('//PackageReference'))) {
            $declaredVersion = if ($reference.Version) {
                [string] $reference.Version
            }
            else {
                $versionNode = $reference.SelectSingleNode('Version')
                if ($null -eq $versionNode) { '' } else { [string] $versionNode.InnerText }
            }
            if ($declaredVersion -cnotmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
                throw "Package '$($reference.Include)' in $($projectFile.Name) must use an exact stable version."
            }
        }
    }
    [xml] $applicationProject = Get-Content -LiteralPath (Get-ApplicationProject) -Raw
    $runtimeVersions = @($applicationProject.SelectNodes('/Project/PropertyGroup/RuntimeFrameworkVersion') |
        ForEach-Object { $_.InnerText } | Where-Object { $_ })
    if ($runtimeVersions.Count -ne 1 -or $runtimeVersions[0] -cne '10.0.8') {
        throw 'The self-contained .NET runtime and shipped notices must remain pinned to 10.0.8.'
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
            if ($contents -match 'actions/checkout@' -and
                $contents -notmatch '(?m)^\s+persist-credentials:\s*false\s*$') {
                throw "Workflow checkout must disable persisted credentials: $($workflow.Name)"
            }
            if ($contents -match '(?m)^\s*pull_request\s*:' -and
                $contents -match '\$\{\{\s*secrets\.') {
                throw "Pull-request workflows must not reference repository secrets: $($workflow.Name)"
            }
            if ($workflow.Name -cne 'release.yml' -and
                $contents -match '(?m)^\s+(contents|id-token|attestations):\s*write\s*$') {
                throw "Write permissions are reserved for the protected release workflow: $($workflow.Name)"
            }
            if ($workflow.Name -ceq 'release.yml' -and
                ($contents -match '(?m)^\s*workflow_dispatch\s*:' -or
                 $contents -notmatch '(?m)^\s+environment:\s*release\s*$' -or
                 $contents -match '--clobber')) {
                throw 'Release workflow must be tag-only, environment-protected, and non-clobbering.'
            }
            if ($workflow.Name -ceq 'release.yml') {
                if ($contents -match '(?i)azure/login|AzureTrustedSign|AZURE_(?:CLIENT|TENANT|SUBSCRIPTION|SIGNING)|EXPECTED_PUBLISHER_SUBJECT') {
                    throw 'The release workflow must not depend on Azure or paid Authenticode signing.'
                }
                $secretReferences = @([regex]::Matches(
                    $contents,
                    '\$\{\{\s*secrets\.([A-Za-z0-9_]+)\s*\}\}') |
                    ForEach-Object { $_.Groups[1].Value } |
                    Sort-Object -Unique)
                if ($secretReferences.Count -ne 1 -or
                    $secretReferences[0] -cne 'UPDATE_SIGNING_PRIVATE_KEY_PEM') {
                    throw 'The release workflow may use only the protected descriptor-signing key.'
                }
                if ($contents -notmatch '(?m)^\s+artifact-metadata:\s*write\s*$' -or
                    $contents -notmatch 'actions/attest@') {
                    throw 'The release publication job must retain GitHub artifact attestation permissions.'
                }
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
        foreach ($projectFile in $projects) {
            $lockFile = Join-Path $projectFile.DirectoryName 'packages.lock.json'
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
