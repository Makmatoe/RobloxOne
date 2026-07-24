# Maintainer release guide

SessionDock releases are tag-triggered, environment-approved, Authenticode
signed, descriptor-signed, checksummed, attested, re-downloaded, and separately
approved before publication. There is no unsigned production fallback and no
local production packaging path.

## Required repository controls

Keep `main` and `v*` protected, Actions defaults read-only, SHA pinning enabled,
dependency review required, vulnerability/secret scanning enabled, and both
`release` and `release-publication` protected by an explicitly chosen reviewer.
Use this idempotent audit before provisioning or changing those controls:

```powershell
./scripts/Configure-GitHubSecurity.ps1 -WhatIf
```

The current one-maintainer repository may allow the named reviewer to approve
their own deployment; do not enable prevent-self-review until another trusted
reviewer exists.

## Managed signing prerequisites

The protected `release` environment must expose these repository or environment
variables. They are identifiers and policy values, not private keys:

- `AZURE_CLIENT_ID`: Entra application/client GUID used only through GitHub OIDC.
- `AZURE_TENANT_ID`: Entra tenant GUID.
- `AZURE_SUBSCRIPTION_ID`: Azure subscription GUID.
- `ARTIFACT_SIGNING_ENDPOINT`: exact regional Azure Artifact Signing HTTPS
  endpoint, such as the endpoint shown on the signing-account resource.
- `ARTIFACT_SIGNING_ACCOUNT_NAME`: Artifact Signing account name.
- `ARTIFACT_SIGNING_CERTIFICATE_PROFILE_NAME`: approved public-trust code-signing
  profile.
- `AUTHENTICODE_PUBLISHER_SUBJECT`: exact `SignerCertificate.Subject` Windows
  must show for both `SessionDock.exe` and Setup.
- `UPDATE_SIGNING_KEY_VAULT_NAME`: Key Vault or Managed HSM vault name containing
  the existing SessionDock descriptor key.
- `UPDATE_SIGNING_KEY_NAME`: P-256 key name whose public half matches
  `SessionDock/Resources/update-public-key.pem`.
- `UPDATE_SIGNING_KEY_VERSION`: immutable 32-hex-character key version.

Create an Entra workload identity federation rule scoped to
`Makmatoe/SessionDock` and the protected `release` environment. Grant only the
Artifact Signing signer role on the selected certificate profile and the key
sign permission on the one P-256 descriptor key/version. Do not create or store
a client secret. The private Authenticode and descriptor keys must remain in
the managed services.

The release job validates every variable before OIDC login. It uses
`Azure/login`, Azure Artifact Signing, and `az keyvault key sign --algorithm
ES256`. Missing configuration, wrong publisher, missing timestamp, a different
key, or an unavailable managed signer stops the release before a draft is
published.

The signed publisher subject is not invented in source. Once the real
certificate profile is provisioned, record its exact subject in
`AUTHENTICODE_PUBLISHER_SUBJECT` and in the release announcement. First-time
users must see that verified publisher; **Unknown publisher** is a failure for
new releases.

## HandleScope release authorization prerequisite

SessionDock has a distinct HandleScope descriptor identity and never reuses the
SessionDock update key. Before enabling HandleScope installation:

1. Create a production P-256 signing key in a managed HSM/KMS. Never export or
   commit the private key.
2. Add only its public key to
   `SessionDock/Resources/handlescope-release-public-keys.json` as an array of
   objects with exact `keyId` and `publicKeyPem` fields. The key ID must follow
   `handlescope-release-YYYY-MM`. A later rotation adds a new explicit entry;
   it does not silently replace an old key.
3. In the HandleScope release producer, create `handlescope-release.json` with
   the exact contract implemented by
   `HandleScopeReleaseAuthorizationPolicy`: schema/product/repository/channel,
   key ID, stable version/tag, UTC publication time, package and checksum names,
   sizes and uppercase SHA-256 values, `CONTENTS.sha256` SHA-256, `windows`,
   `x64`, and a 64-byte P-256 P1363 signature.
4. Sign SHA-256 of the canonical newline-delimited payload with managed ES256,
   encode the raw 64-byte signature as standard Base64 in the descriptor, and
   publish the descriptor beside the ZIP and `SHA256SUMS.txt` in one stable,
   immutable HandleScope release.

There is no sibling HandleScope repository in this workspace, so its producer
workflow cannot be changed here. Until the genuine public key and signed asset
exist, SessionDock deliberately reports that HandleScope installation is
unavailable. Test keys remain only in the test assembly.

## Prepare and validate

Use .NET SDK 10.0.302 and the self-contained .NET 10.0.10 runtime pinned by the
repository. Before tagging:

```powershell
dotnet --info
dotnet restore SessionDock.slnx --locked-mode
./scripts/Build.ps1 -Configuration Release -Runtime win-x64 `
    -OutputDirectory artifacts/release-validation -CI
./scripts/Build-RuntimeSmoke.ps1 `
    -OutputDirectory artifacts/release-runtime-smoke -TimeoutSeconds 30
./scripts/Test-DotNetSecurityPatch.ps1 -CheckOnline
./scripts/Verify-Release.ps1 -Tag vX.Y.Z
```

The runtime smoke is compiled only with `EnableRuntimeSmokeHarness=true` into a
separate directory. `Build.ps1` then publishes the production executable with
that feature absent, and `Verify-Publish.ps1` scans the production binary for
the removed switch. No smoke build is staged as release input.

## Protected workflow order

After an annotated `vX.Y.Z` tag is pushed from the protected `main` tip, the
workflow:

1. validates metadata, locked restore, NuGet audit, tests, production publish,
   and the separate test-only runtime smoke;
2. enters the reviewer-gated `release` environment and obtains Azure access by
   OIDC;
3. Authenticode-signs and verifies the project-owned `SessionDock.exe`,
   including exact publisher and timestamp;
4. packages that signed executable, then Authenticode-signs the final Setup
   before hashes or descriptor creation;
5. prepares the canonical unsigned update descriptor and its SHA-256 digest,
   asks the pinned Key Vault P-256 key to sign only that digest, completes the
   descriptor, and verifies it with the embedded public key;
6. verifies Authenticode inside the NUPKG and portable ZIP plus the final Setup,
   then creates the SBOM and final checksums;
7. creates a fresh draft, uploads, re-downloads, byte-compares, and attests all
   assets;
8. waits for separate `release-publication` approval, re-downloads again,
   verifies exact inventory/checksums/attestations and all required
   Authenticode signatures, and only then publishes.

Never mutate an executable, package, descriptor, Setup, SBOM, or checksum file
after the stage that binds it. A failed unpublished draft must be investigated
and explicitly removed before retrying; a published tag or asset is never
reused.

## User verification

Before announcing a release, confirm Windows reports the exact configured
publisher instead of Unknown publisher, the in-app updater verifies the signed
descriptor, the public release is immutable, and the manual checksum and
GitHub attestation commands in [UPDATES.md](UPDATES.md) succeed.
