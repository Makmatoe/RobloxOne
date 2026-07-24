[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v\d+\.\d+\.\d+$')]
    [string] $Tag,

    [string] $OutputDirectory = 'artifacts/release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

throw @"
Local production release packaging is intentionally disabled. SessionDock
releases require the protected GitHub release environment, the protected P-256
update-descriptor key, complete checksums, attestations, and separate approval.
The Windows executables are intentionally unsigned and must be distributed only
through the canonical GitHub release workflow.
Use scripts/Verify-Release.ps1 for a non-publishing local policy check. The
tag-triggered .github/workflows/release.yml workflow is the only production
packaging and publication path.
"@
