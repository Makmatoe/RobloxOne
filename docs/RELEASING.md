# Maintainer release guide

SessionDock releases are tag-triggered, environment-approved,
descriptor-signed, checksummed, attested, re-downloaded, and separately approved
before publication. The Windows executables and Setup are currently unsigned
because the project does not have a paid Authenticode certificate. Windows may
therefore show **Unknown publisher** or a SmartScreen warning.

Unsigned does not mean unverified. The release workflow retains the controls
that can operate without a commercial certificate: a signed update descriptor,
exact package hashes, package-content allowlists, SBOM, checksums, GitHub
attestations, immutable draft re-download, and a separate publication approval.
None of those controls provides Windows publisher identity.

## Required repository controls

Keep `main` and `v*` protected, Actions defaults read-only, SHA pinning enabled,
dependency review required, vulnerability and secret scanning enabled, and both
`release` and `release-publication` protected by an explicitly chosen reviewer.
Audit them with:

```powershell
./scripts/Configure-GitHubSecurity.ps1 -WhatIf
```

The current one-maintainer repository may allow the named reviewer to approve
their own deployment. Do not enable prevent-self-review until another trusted
reviewer exists.

## Required update-descriptor key

The protected `release` environment uses exactly one repository secret:

```text
UPDATE_SIGNING_PRIVATE_KEY_PKCS8_BASE64
```

It contains the Base64-encoded PKCS#8 form of the P-256 private key whose public
half is pinned at `SessionDock/Resources/update-public-key.pem`. GitHub never
returns a stored secret value. Keep an offline recovery copy outside the
repository and outside ordinary build machines.

The workflow exposes this secret only to the reviewer-gated staging job and
uses it only to sign SHA-256 of the canonical update-descriptor payload. The
script validates P-256, emits a fixed-width P1363 signature, verifies the
completed descriptor with the public key, removes the environment variable,
and clears decoded key bytes. The private key is never written to a release
asset or committed file. The final publication job receives no secrets.

This is less isolated than an HSM-backed signer, but preserves the updater's
cryptographic package authorization without requiring a commercial Windows
code-signing certificate. Never use this key to sign executables or HandleScope
releases.

## HandleScope release authorization prerequisite

SessionDock uses a separate HandleScope descriptor identity and never reuses
the SessionDock update key. Before enabling HandleScope installation:

1. Create a production P-256 signing key that is distinct from the SessionDock
   update key. Prefer a managed KMS where available; never commit its private
   half.
2. Add only its public key to
   `SessionDock/Resources/handlescope-release-public-keys.json` as an array of
   objects with exact `keyId` and `publicKeyPem` fields. Key IDs use
   `handlescope-release-YYYY-MM`. Rotation adds a new explicit entry rather than
   silently replacing an old key.
3. In the HandleScope release producer, create `handlescope-release.json` with
   the exact contract implemented by `HandleScopeReleaseAuthorizationPolicy`:
   schema/product/repository/channel, key ID, stable version/tag, UTC publication
   time, package and checksum names, sizes and uppercase SHA-256 values,
   `CONTENTS.sha256` SHA-256, `windows`, `x64`, and a 64-byte P-256 P1363
   signature.
4. Sign SHA-256 of the canonical newline-delimited payload, encode the raw
   64-byte signature as standard Base64, and publish the descriptor beside the
   ZIP and `SHA256SUMS.txt` in one stable immutable HandleScope release.

There is no sibling HandleScope checkout in this workspace. Until its genuine
public key and signed descriptor exist, SessionDock reports that HandleScope
installation is unavailable and runs no downloaded installer.

## Prepare and validate

Use the pinned .NET SDK 10.0.302 and self-contained runtime 10.0.10. Before
tagging:

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

The smoke feature is compiled only into a separate test artifact. Production
publish verification proves the privileged smoke switch is absent.

## Protected workflow order

After an annotated `vX.Y.Z` tag is pushed from the protected `main` tip, the
workflow:

1. validates release metadata, locked restore, NuGet audit, tests, production
   publish, and the separate smoke build;
2. enters the reviewer-gated `release` environment;
3. packages the verified but unsigned production application;
4. prepares the canonical update descriptor and signs its digest with the
   protected P-256 descriptor key;
5. verifies the descriptor, exact package hash and package/portable contents;
6. generates the SBOM and complete SHA-256 checksums;
7. creates a fresh draft, uploads, re-downloads, byte-compares, and attests all
   assets;
8. waits for `release-publication` approval, then re-downloads and verifies the
   exact inventory, checksums, attestations, source tag and commit before making
   the release public.

Never mutate an executable, package, descriptor, Setup, SBOM, or checksum after
the stage that binds it. Investigate and explicitly remove only a failed
unpublished draft before retrying. Never reuse a published tag or asset.

## User verification

Before announcing a release, confirm the in-app updater accepts the signed
descriptor and the manual checksum and GitHub attestation commands in
`docs/UPDATES.md` succeed. Tell users plainly that Windows will show Unknown
publisher and that checksums or attestations should be verified before they
continue through that warning.
