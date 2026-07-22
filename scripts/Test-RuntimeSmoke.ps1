[CmdletBinding()]
param(
    [string] $ApplicationPath = 'artifacts/runtime-smoke/SessionDock.exe',

    [ValidateRange(1, 120)]
    [int] $TimeoutSeconds = 20
)

. (Join-Path $PSScriptRoot 'Common.ps1')

$SmokeDirectoryPrefix = 'SessionDock-runtime-smoke-'
$SuccessMarkerName = 'runtime-smoke.success'

function Test-IsLinkOrReparsePoint {
    param(
        [Parameter(Mandatory)]
        [IO.FileSystemInfo] $Item
    )

    # FileAttributes is the authoritative Windows check here. PowerShell 5.1
    # can report DirectoryInfo.Target as the directory itself for the normal
    # per-user Temp folder, which is not a reparse point.
    return ($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
}

function Assert-IsolatedSmokeRoot {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $ExpectedName,

        [Parameter(Mandatory)]
        [string] $TemporaryDirectory
    )

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $temporaryRoot = [IO.Path]::GetFullPath($TemporaryDirectory).TrimEnd('\', '/')
    $parent = [IO.Path]::GetDirectoryName($fullPath)
    $name = [IO.Path]::GetFileName($fullPath)
    if (-not $parent.Equals(
            $temporaryRoot,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not $name.Equals(
            $ExpectedName,
            [StringComparison]::Ordinal) -or
        $name -cnotmatch '^SessionDock-runtime-smoke-[0-9a-f]{32}$') {
        throw "Refusing unsafe runtime-smoke path: $fullPath"
    }

    return $fullPath
}

function Assert-TreeContainsNoLinks {
    param(
        [Parameter(Mandatory)]
        [string] $Root
    )

    $pending = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    $pending.Push([IO.DirectoryInfo]::new($Root))
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        if (Test-IsLinkOrReparsePoint $directory) {
            throw "Runtime-smoke directory is a symbolic link or junction: $($directory.FullName)"
        }

        foreach ($entry in $directory.EnumerateFileSystemInfos()) {
            if (Test-IsLinkOrReparsePoint $entry) {
                throw "Refusing to remove runtime-smoke data containing a symbolic link or junction: $($entry.FullName)"
            }
            if ($entry -is [IO.DirectoryInfo]) {
                $pending.Push($entry)
            }
        }
    }
}

function Remove-IsolatedSmokeRoot {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $ExpectedName,

        [Parameter(Mandatory)]
        [string] $TemporaryDirectory
    )

    $safePath = Assert-IsolatedSmokeRoot `
        -Path $Path `
        -ExpectedName $ExpectedName `
        -TemporaryDirectory $TemporaryDirectory
    if (-not (Test-Path -LiteralPath $safePath)) {
        return
    }

    $item = Get-Item -LiteralPath $safePath -Force
    if (-not $item.PSIsContainer) {
        throw "Runtime-smoke cleanup target is not a directory: $safePath"
    }
    Assert-TreeContainsNoLinks -Root $safePath

    $lastFailure = $null
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        try {
            Remove-Item -LiteralPath $safePath -Recurse -Force
            return
        }
        catch [IOException], [UnauthorizedAccessException] {
            $lastFailure = $_.Exception
            Start-Sleep -Milliseconds 100
        }
    }

    throw [IOException]::new(
        "The isolated runtime-smoke directory could not be removed: $safePath",
        $lastFailure)
}

$repositoryRoot = Get-RepositoryRoot
$candidatePath = if ([IO.Path]::IsPathRooted($ApplicationPath)) {
    $ApplicationPath
}
else {
    Join-Path $repositoryRoot $ApplicationPath
}
$application = [IO.Path]::GetFullPath($candidatePath)
if (-not (Test-Path -LiteralPath $application -PathType Leaf)) {
    throw "Published SessionDock executable not found: $application"
}
$applicationItem = Get-Item -LiteralPath $application -Force
if (Test-IsLinkOrReparsePoint $applicationItem) {
    throw "Published SessionDock executable must not be a symbolic link: $application"
}

$temporaryDirectory = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$temporaryDirectoryItem = Get-Item -LiteralPath $temporaryDirectory -Force
if (-not $temporaryDirectoryItem.PSIsContainer -or
    (Test-IsLinkOrReparsePoint $temporaryDirectoryItem)) {
    throw 'The Windows temporary directory must be a regular directory.'
}
$smokeName = "$SmokeDirectoryPrefix$([Guid]::NewGuid().ToString('N'))"
$smokeRoot = Assert-IsolatedSmokeRoot `
    -Path (Join-Path $temporaryDirectory $smokeName) `
    -ExpectedName $smokeName `
    -TemporaryDirectory $temporaryDirectory
if (Test-Path -LiteralPath $smokeRoot) {
    throw "Generated runtime-smoke directory already exists: $smokeRoot"
}

$process = $null
$cleanupAllowed = $true
try {
    # The generated path cannot contain a quote and does not end in a slash,
    # so quoting it preserves spaces when Start-Process builds the command line.
    $quotedSmokeRoot = '"' + $smokeRoot + '"'
    $process = Start-Process `
        -FilePath $application `
        -ArgumentList @('--isolated-runtime-smoke', $quotedSmokeRoot) `
        -WindowStyle Hidden `
        -PassThru

    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $terminated = $false
        try {
            $process.Kill()
            $terminated = $process.WaitForExit(5000)
        }
        catch [InvalidOperationException] {
            # The exact process exited between the timeout and termination.
            $terminated = $process.HasExited
        }
        if (-not $terminated) {
            $cleanupAllowed = $false
            throw "SessionDock runtime smoke did not terminate after its timeout. The isolated data was retained at: $smokeRoot"
        }
        throw "SessionDock runtime smoke exceeded the $TimeoutSeconds-second limit."
    }

    if ($process.ExitCode -ne 0) {
        throw "SessionDock runtime smoke exited with code $($process.ExitCode)."
    }

    $successMarker = Join-Path $smokeRoot $SuccessMarkerName
    $settingsPath = Join-Path $smokeRoot 'settings.json'
    $soundsPath = Join-Path $smokeRoot 'Sounds'
    if (-not (Test-Path -LiteralPath $successMarker -PathType Leaf) -or
        (Get-Item -LiteralPath $successMarker -Force).Length -le 0) {
        throw "SessionDock did not write the nonempty runtime-smoke success marker: $successMarker"
    }
    if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
        throw "SessionDock did not persist isolated settings: $settingsPath"
    }
    if (-not (Test-Path -LiteralPath $soundsPath -PathType Container)) {
        throw "SessionDock did not create the isolated sound directory: $soundsPath"
    }

    foreach ($requiredItem in @($successMarker, $settingsPath, $soundsPath)) {
        $item = Get-Item -LiteralPath $requiredItem -Force
        if (Test-IsLinkOrReparsePoint $item) {
            throw "Runtime-smoke output must not be a symbolic link or junction: $requiredItem"
        }
    }

    Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json | Out-Null
    Write-Host "SessionDock isolated runtime smoke passed in $($process.ExitTime - $process.StartTime)."
}
finally {
    if ($null -ne $process) {
        try {
            if (-not $process.HasExited) {
                $cleanupAllowed = $false
            }
        }
        catch [InvalidOperationException] {
            $cleanupAllowed = $false
        }
        $process.Dispose()
    }
    if ($cleanupAllowed) {
        Remove-IsolatedSmokeRoot `
            -Path $smokeRoot `
            -ExpectedName $smokeName `
            -TemporaryDirectory $temporaryDirectory
    }
    else {
        Write-Warning "The isolated runtime-smoke process may still be running. Its validated temporary data was not removed: $smokeRoot"
    }
}
