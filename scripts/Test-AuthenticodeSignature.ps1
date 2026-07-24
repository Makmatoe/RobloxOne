[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]] $Path,

    [Parameter(Mandatory)]
    [string] $ExpectedPublisherSubject
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ExpectedPublisherSubject) -or
    $ExpectedPublisherSubject.Length -gt 1024 -or
    $ExpectedPublisherSubject -match '[\x00-\x1F\x7F]') {
    throw 'The expected Authenticode publisher subject is missing or invalid.'
}

foreach ($candidate in $Path) {
    $fullPath = [IO.Path]::GetFullPath($candidate)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Required signed executable not found: $fullPath"
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $fullPath
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate) {
        throw "Authenticode validation failed for $([IO.Path]::GetFileName($fullPath)): $($signature.Status)."
    }
    if ($signature.SignerCertificate.Subject -cne $ExpectedPublisherSubject) {
        throw "Unexpected Authenticode publisher for $([IO.Path]::GetFileName($fullPath)): '$($signature.SignerCertificate.Subject)'."
    }
    $codeSigningEku = @($signature.SignerCertificate.Extensions |
        Where-Object { $_.Oid.Value -eq '2.5.29.37' } |
        ForEach-Object { $_.EnhancedKeyUsages } |
        Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.3' })
    if ($codeSigningEku.Count -eq 0) {
        throw "The Authenticode certificate for $([IO.Path]::GetFileName($fullPath)) is not authorized for code signing."
    }
    if ($null -eq $signature.TimeStamperCertificate) {
        throw "The Authenticode signature for $([IO.Path]::GetFileName($fullPath)) has no trusted timestamp."
    }
}

Write-Host "Verified valid timestamped Authenticode signatures for $($Path.Count) file(s)."
