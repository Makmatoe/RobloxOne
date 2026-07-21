[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v\d+\.\d+\.\d+$')]
    [string] $Tag,

    [switch] $RequireTagAtHead,

    [switch] $RequireMainAtHead,

    [switch] $RequireAnnotatedTag,

    [switch] $RequireCleanWorkingTree
)

. (Join-Path $PSScriptRoot 'Common.ps1')

$root = Get-RepositoryRoot
Push-Location $root
try {
    & (Join-Path $PSScriptRoot 'Verify-Repository.ps1') -CI

    $version = Get-ProjectVersion
    if ($Tag -cne "v$version") {
        throw "Tag '$Tag' must exactly match project version '$version' as v$version."
    }

    $notesPath = Join-Path $root "RobloxOneLauncher/ReleaseNotes/$version.md"
    if (-not (Test-Path -LiteralPath $notesPath -PathType Leaf)) {
        throw "Release notes are required at RobloxOneLauncher/ReleaseNotes/$version.md."
    }

    $notes = Get-Content -LiteralPath $notesPath -Raw
    if ([string]::IsNullOrWhiteSpace($notes) -or $notes.Length -gt 65536) {
        throw 'Release notes must contain between 1 and 65,536 characters.'
    }
    if ($notes -match '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]') {
        throw 'Release notes contain unsupported control characters.'
    }

    $head = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -cnotmatch '^[0-9a-f]{40}$') {
        throw 'Unable to resolve the current Git commit.'
    }

    if ($RequireTagAtHead) {
        $tagCommit = (& git rev-list -n 1 "refs/tags/$Tag").Trim()
        if ($LASTEXITCODE -ne 0 -or $tagCommit -ne $head) {
            throw "Tag '$Tag' does not resolve to the checked-out commit."
        }
    }

    if ($RequireAnnotatedTag) {
        $tagType = (& git cat-file -t "refs/tags/$Tag").Trim()
        if ($LASTEXITCODE -ne 0 -or $tagType -cne 'tag') {
            throw "Release tag '$Tag' must be an annotated tag object."
        }
    }

    if ($RequireMainAtHead) {
        $mainCommit = (& git rev-parse 'refs/remotes/origin/main').Trim()
        if ($LASTEXITCODE -ne 0 -or $mainCommit -ne $head) {
            throw "Release tags must point at the current origin/main commit. HEAD=$head origin/main=$mainCommit"
        }
    }

    if ($RequireCleanWorkingTree) {
        $changes = @(& git status --porcelain=v1 --untracked-files=all)
        if ($LASTEXITCODE -ne 0) {
            throw 'Unable to inspect the release working tree.'
        }
        if ($changes.Count -ne 0) {
            throw "Release working tree must be clean:`n$($changes -join [Environment]::NewLine)"
        }
    }

    Write-Host "Release metadata is aligned for $Tag at $head."
}
finally {
    Pop-Location
}
