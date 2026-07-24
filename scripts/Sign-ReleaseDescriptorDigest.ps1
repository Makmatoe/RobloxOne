[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $DigestPath,

    [Parameter(Mandatory)]
    [string] $SignaturePath,

    [string] $PrivateKeyPkcs8Base64 =
        $env:UPDATE_SIGNING_PRIVATE_KEY_PKCS8_BASE64
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -cne 'Core' -or
    $PSVersionTable.PSVersion -lt [version] '7.4') {
    throw 'Update-descriptor signing requires PowerShell 7.4 or later.'
}

$digestFullPath = [IO.Path]::GetFullPath($DigestPath)
$signatureFullPath = [IO.Path]::GetFullPath($SignaturePath)
if (-not (Test-Path -LiteralPath $digestFullPath -PathType Leaf)) {
    throw 'The canonical update-descriptor digest file is missing.'
}
$digest = (Get-Content -LiteralPath $digestFullPath -Raw).Trim()
if ($digest -cnotmatch '^[A-Za-z0-9_-]{43}$') {
    throw 'The descriptor payload digest is not canonical SHA-256 base64url.'
}
if ([string]::IsNullOrWhiteSpace($PrivateKeyPkcs8Base64) -or
    $PrivateKeyPkcs8Base64 -match '\s') {
    throw 'The protected update-descriptor signing key is missing or malformed.'
}

$keyBytes = $null
$digestBytes = $null
$signatureBytes = $null
$key = $null
try {
    $keyBytes = [Convert]::FromBase64String($PrivateKeyPkcs8Base64)
    $digestBytes = [Convert]::FromBase64String(
        $digest.Replace('-', '+').Replace('_', '/') + '=')
    $key = [Security.Cryptography.ECDsa]::Create()
    $bytesRead = 0
    $key.ImportPkcs8PrivateKey($keyBytes, [ref] $bytesRead)
    if ($bytesRead -ne $keyBytes.Length -or $key.KeySize -ne 256) {
        throw 'The protected update-descriptor key is not one exact P-256 PKCS#8 key.'
    }
    $signatureBytes = $key.SignHash(
        $digestBytes,
        [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
    if ($signatureBytes.Length -ne 64) {
        throw 'The update-descriptor signer did not return one P-256 signature.'
    }
    $signature = [Convert]::ToBase64String($signatureBytes)
    $signature = $signature.TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $parent = [IO.Path]::GetDirectoryName($signatureFullPath)
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        [IO.Directory]::CreateDirectory($parent) | Out-Null
    }
    Set-Content -LiteralPath $signatureFullPath `
        -Value $signature -Encoding ascii -NoNewline
}
finally {
    if ($null -ne $key) { $key.Dispose() }
    if ($null -ne $signatureBytes) {
        [Security.Cryptography.CryptographicOperations]::ZeroMemory(
            $signatureBytes)
    }
    if ($null -ne $digestBytes) {
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($digestBytes)
    }
    if ($null -ne $keyBytes) {
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($keyBytes)
    }
    Remove-Item Env:UPDATE_SIGNING_PRIVATE_KEY_PKCS8_BASE64 `
        -ErrorAction SilentlyContinue
}

Write-Host 'Created one canonical external update-descriptor signature.'
