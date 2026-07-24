[CmdletBinding()]
param(
    [string] $AzureClientId = $env:AZURE_CLIENT_ID,
    [string] $AzureTenantId = $env:AZURE_TENANT_ID,
    [string] $AzureSubscriptionId = $env:AZURE_SUBSCRIPTION_ID,
    [string] $ArtifactSigningEndpoint = $env:ARTIFACT_SIGNING_ENDPOINT,
    [string] $ArtifactSigningAccountName = $env:ARTIFACT_SIGNING_ACCOUNT_NAME,
    [string] $ArtifactSigningCertificateProfileName =
        $env:ARTIFACT_SIGNING_CERTIFICATE_PROFILE_NAME,
    [string] $AuthenticodePublisherSubject =
        $env:AUTHENTICODE_PUBLISHER_SUBJECT,
    [string] $UpdateSigningKeyVaultName =
        $env:UPDATE_SIGNING_KEY_VAULT_NAME,
    [string] $UpdateSigningKeyName = $env:UPDATE_SIGNING_KEY_NAME,
    [string] $UpdateSigningKeyVersion = $env:UPDATE_SIGNING_KEY_VERSION
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$identifiers = [ordered]@{
    AZURE_CLIENT_ID = $AzureClientId
    AZURE_TENANT_ID = $AzureTenantId
    AZURE_SUBSCRIPTION_ID = $AzureSubscriptionId
}
foreach ($entry in $identifiers.GetEnumerator()) {
    if ($entry.Value -cnotmatch '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$') {
        throw "Managed release signing is not configured: repository variable $($entry.Key) must be one GUID."
    }
}

$endpoint = $null
if (-not [Uri]::TryCreate(
        $ArtifactSigningEndpoint,
        [UriKind]::Absolute,
        [ref] $endpoint) -or
    $endpoint.Scheme -cne 'https' -or
    -not $endpoint.IsDefaultPort -or
    -not [string]::IsNullOrEmpty($endpoint.UserInfo) -or
    -not [string]::IsNullOrEmpty($endpoint.Query) -or
    -not [string]::IsNullOrEmpty($endpoint.Fragment) -or
    $endpoint.Host -cnotmatch '^[a-z0-9-]+\.codesigning\.azure\.net$') {
    throw 'Managed release signing is not configured: ARTIFACT_SIGNING_ENDPOINT must be the exact regional Azure Artifact Signing HTTPS endpoint.'
}

$names = [ordered]@{
    ARTIFACT_SIGNING_ACCOUNT_NAME = $ArtifactSigningAccountName
    ARTIFACT_SIGNING_CERTIFICATE_PROFILE_NAME =
        $ArtifactSigningCertificateProfileName
    UPDATE_SIGNING_KEY_VAULT_NAME = $UpdateSigningKeyVaultName
    UPDATE_SIGNING_KEY_NAME = $UpdateSigningKeyName
}
foreach ($entry in $names.GetEnumerator()) {
    if ($entry.Value -cnotmatch '^[A-Za-z0-9][A-Za-z0-9-]{1,126}[A-Za-z0-9]$') {
        throw "Managed release signing is not configured: repository variable $($entry.Key) is missing or invalid."
    }
}
if ($UpdateSigningKeyVersion -cnotmatch '^[0-9a-fA-F]{32}$') {
    throw 'Managed release signing is not configured: UPDATE_SIGNING_KEY_VERSION must pin one immutable 32-hex-character Azure Key Vault key version.'
}
if ([string]::IsNullOrWhiteSpace($AuthenticodePublisherSubject) -or
    $AuthenticodePublisherSubject.Length -gt 1024 -or
    $AuthenticodePublisherSubject -match '[\x00-\x1F\x7F]') {
    throw 'Managed release signing is not configured: AUTHENTICODE_PUBLISHER_SUBJECT must be the exact certificate subject shown by Windows.'
}

Write-Host 'Managed Authenticode and descriptor signing configuration is structurally complete.'
