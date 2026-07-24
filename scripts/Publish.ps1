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
releases require the protected GitHub release environment, Azure OIDC,
Azure Artifact Signing, and the pinned Azure Key Vault P-256 descriptor key.
Use scripts/Verify-Release.ps1 for a non-publishing local policy check. The
tag-triggered .github/workflows/release.yml workflow is the only production
packaging and publication path.
"@
