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

    $notesPath = Join-Path $root "SessionDock/ReleaseNotes/$version.md"
    if (-not (Test-Path -LiteralPath $notesPath -PathType Leaf)) {
        throw "Release notes are required at SessionDock/ReleaseNotes/$version.md."
    }

    $notes = Get-Content -LiteralPath $notesPath -Raw
    if ([string]::IsNullOrWhiteSpace($notes) -or $notes.Length -gt 65536) {
        throw 'Release notes must contain between 1 and 65,536 characters.'
    }
    if ($notes -match '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]') {
        throw 'Release notes contain unsupported control characters.'
    }

    # Version 2.3.0 displays signed notes in a plain TextBox and can update
    # directly to any later release. Keep every future notes file readable in
    # that legacy dialog even though newer clients also apply local formatting.
    $plainTextCompatibilityPatterns = [ordered] @{
        'ATX heading markers' = '(?m)^[ \t]{0,3}#{1,6}(?:[ \t]+|$)'
        'indented continuation or code lines' = '(?m)^[ \t]+\S'
        'block quote markers' = '(?m)^[ \t]{0,3}>'
        'fenced code or horizontal rules' = '(?m)^[ \t]{0,3}(?:`{3,}|~{3,}|-{3,}|\*{3,}|_{3,})[ \t]*$'
        'inline code markers' = '`'
        'emphasis markers' = '(?:\*\*|\*[^*\r\n]+\*|__|(?<!\w)_[^_\r\n]+_(?!\w))'
        'Markdown links or images' = '!?\[[^\]\r\n]*\]\([^\)\r\n]+\)'
        'raw HTML' = '<[!/A-Za-z][^>\r\n]*>'
    }
    foreach ($entry in $plainTextCompatibilityPatterns.GetEnumerator()) {
        if ($notes -match $entry.Value) {
            throw "Release notes contain $($entry.Key), which are not readable in the 2.3.0 plain-text update dialog. Use plain headings and single-line '- ' bullets."
        }
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
