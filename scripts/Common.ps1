Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RepositoryRoot {
    return (Split-Path -Parent $PSScriptRoot)
}

function Get-ApplicationProject {
    return (Join-Path (Get-RepositoryRoot) 'SessionDock/SessionDock.csproj')
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

function Assert-LegacyReadableReleaseNotes {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Release notes are required: $Path"
    }

    $notes = Get-Content -LiteralPath $Path -Raw
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

function Test-PathEntryIsLink {
    param(
        [Parameter(Mandatory)]
        [IO.FileSystemInfo] $Item
    )

    foreach ($propertyName in @('LinkType', 'LinkTarget', 'Target')) {
        $property = $Item.PSObject.Properties[$propertyName]
        if ($null -ne $property -and
            -not [string]::IsNullOrEmpty([string] $property.Value)) {
            return $true
        }
    }

    return $false
}

function Assert-SafeOutputDirectory {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $root = [IO.Path]::GetFullPath((Get-RepositoryRoot)).TrimEnd('\', '/')
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $artifactsRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts')).TrimEnd('\', '/')
    $artifactsPrefix = "$artifactsRoot$([IO.Path]::DirectorySeparatorChar)"
    if (-not $fullPath.StartsWith(
            $artifactsPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Output directory must be a child directory of $artifactsRoot. Received: $fullPath"
    }
    $relativePath = $fullPath.Substring($artifactsPrefix.Length)

    $current = $artifactsRoot
    $separators = [char[]] @(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    foreach ($component in $relativePath.Split(
            $separators,
            [StringSplitOptions]::RemoveEmptyEntries)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (Test-PathEntryIsLink $item) {
                throw "Output directory crosses a symbolic link or junction: $($item.FullName)"
            }
        }
        $current = Join-Path $current $component
    }

    if (Test-Path -LiteralPath $current) {
        $item = Get-Item -LiteralPath $current -Force
        if (Test-PathEntryIsLink $item) {
            throw "Output directory is a symbolic link or junction: $($item.FullName)"
        }
    }

    return $fullPath
}

function Remove-SafeOutputDirectory {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $fullPath = Assert-SafeOutputDirectory $Path
    if (-not (Test-Path -LiteralPath $fullPath)) {
        return
    }

    $item = Get-Item -LiteralPath $fullPath -Force
    if (-not $item.PSIsContainer) {
        throw "Output path is not a directory: $fullPath"
    }

    $linkedEntry = Get-ChildItem -LiteralPath $fullPath -Force -Recurse |
        Where-Object { Test-PathEntryIsLink $_ } |
        Select-Object -First 1
    if ($null -ne $linkedEntry) {
        throw "Refusing to recursively remove an output tree containing a symbolic link or junction: $($linkedEntry.FullName)"
    }

    Remove-Item -LiteralPath $fullPath -Recurse -Force
}
