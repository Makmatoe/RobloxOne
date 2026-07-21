# Maintainer release guide

Roblox One releases are tag-triggered, reproducible-in-scope builds with two
independent production signatures: an ECDSA-signed release descriptor and
Windows Authenticode signing through Azure Artifact Signing. GitHub-hosted
artifacts and attestations are an additional distribution/provenance layer, not
the root of update trust.

## One-time repository configuration

Configure these controls before the first public release:

1. Review the initial commit while the repository is private. Before distributing
   the app, make the canonical repository public; the updater intentionally has
   no embedded GitHub token and cannot read releases from a private repository.
2. Make `main` the default branch and protect it with a ruleset requiring pull
   requests, passing checks, resolved conversations, and no force pushes or
   deletions.
3. Protect tags matching `v*`; restrict tag creation and prevent updates or
   deletion after publication.
4. Set GitHub Actions' default token permission to read-only. Grant job-specific
   write permissions only in the release workflow.
5. Enable private vulnerability reporting, Dependabot alerts, secret scanning,
   push protection, CodeQL default setup for C#, and immutable releases where
   available. After GitHub Dependency Review is supported and the repository's
   Dependency Graph is enabled, add the repository variable
   `DEPENDENCY_REVIEW_ENABLED=true`. Until then, the CI build still fails on
   vulnerable or deprecated NuGet dependencies through the repository-owned
   audit script.
6. Create a protected `release` environment with a required human reviewer.
   Only the final signing/publishing job may use it. For a single-owner
   repository, allow self-review so releases do not deadlock; this remains an
   explicit publication confirmation, not independent review. As soon as a
   second trusted maintainer is available, require that maintainer and prevent
   self-review.
7. Configure Azure workload identity federation for GitHub Actions. Do not
   store a long-lived Azure client secret.

The repository is not release-ready while any of those controls is absent.
In particular, a private repository cannot serve the tokenless updater, and a
tag must not be pushed until the protected `release` environment is complete.

The production workflow expects these protected environment variables:

- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`
- `AZURE_SIGNING_ENDPOINT`
- `AZURE_SIGNING_ACCOUNT`
- `AZURE_SIGNING_PROFILE`
- `EXPECTED_PUBLISHER_SUBJECT`
- `APPROVED_RELEASE_LICENSE_SHA256`

It also expects `UPDATE_SIGNING_PRIVATE_KEY_PEM` as a protected environment
secret. This key signs only the bounded release descriptor. Limit environment
access, rotate the key deliberately with a new embedded key identifier, and
never print, upload, cache, or persist it. A managed signing service should
replace the repository secret if one becomes available for this descriptor
format.

`APPROVED_RELEASE_LICENSE_SHA256` is the uppercase SHA-256 of an independently
reviewed license that permits binary distribution. The release job also rejects
terms that explicitly prohibit publishing or distribution, even if their hash
is configured. The current repository-only, no-distribution `LICENSE.md` is an
intentional release blocker; do not work around it. Adopt appropriate release
terms with the copyright holder's approval, review them, then update the
protected hash.

## Prepare a release

1. Start from a clean, reviewed `main` commit.
2. Choose a semantic version such as `2.1.0`.
3. Set the project version to exactly that value.
4. Add `ReleaseNotes/2.1.0.md`. Keep notes user-focused, displayable, and free
   of secrets or untrusted HTML.
5. Restore, build, test, and run the repository validation scripts locally.
6. Confirm the publish inventory contains only `RobloxOne.exe`, the approved
   license, `THIRD_PARTY_NOTICES.md`, and the pinned upstream license/notice
   files under `licenses/`.
7. Review dependency vulnerability output, the generated SPDX SBOM, and the
   complete release diff.
8. Confirm no release or draft already exists for the version.
9. Merge through the protected branch after required checks pass.

The project version, notes filename, tag, assembly/package version, descriptor
version, and Velopack version must agree. The release workflow must fail closed
on any mismatch.

## Publish

Create and push an annotated version tag only after the reviewed commit is the
tip of `main`:

```powershell
git switch main
git pull --ff-only
git tag -a v2.1.0 -m "Roblox One 2.1.0"
git push origin v2.1.0
```

The tag workflow should then:

1. verify that the tag commit is the current protected `main` tip;
2. restore locked dependencies and run the full Release build/test suite;
3. publish the self-contained Windows x64 application;
4. use pinned Velopack tooling to build packages;
5. Authenticode-sign production application files through Azure Artifact
   Signing and verify the exact expected publisher;
6. generate and sign the bounded release descriptor, including the final
   package filename, size, SHA-256 digest, channel, key identifier, version, and
   release notes;
7. assemble a draft GitHub Release and verify every staged asset;
8. publish an SPDX 2.3 SBOM and a `SHA256SUMS.txt` that covers every other
   downloadable asset;
9. verify the exact release inventory, Velopack feeds, package/portable entry
   allowlists, approved license, and every executable payload's publisher;
10. create GitHub artifact attestations for final downloadable assets; and
11. publish the immutable release only after all verification succeeds.

Actions must be pinned to full commit SHAs. Release jobs must not execute code
from an untrusted pull request, use `pull_request_target`, or expose the signing
environment to arbitrary workflow inputs. Never rerun a release for an existing
version by moving its tag; issue a new version instead.

The workflow refuses to reuse or overwrite an existing draft. If a run fails
after creating a draft, inspect it, delete only that unpublished draft, and
rerun the unchanged tag. Never use asset clobbering, replace a published asset,
or move the tag. If anything was published, fix forward with a new version.

## Verify after publication

Before announcing the release:

- install the Setup asset on a clean Windows x64 test account or VM;
- verify the Windows signature and publisher;
- confirm the in-app version;
- exercise add/remove account, destination parsing, single launch, Recent, and
  cancellation paths without real credentials in screenshots/logs;
- confirm the top-right update button reports no newer version;
- from the preceding production version, confirm update discovery, signed notes,
  download, restart, retained local data, and final version; and
- verify the GitHub attestation for each published downloadable artifact.

Download into a clean directory and verify provenance plus checksums before
manual testing:

```powershell
gh release download v2.1.0 --repo Makmatoe/RobloxOne --dir verified-release
gh attestation verify verified-release/* --repo Makmatoe/RobloxOne
$lines = Get-Content verified-release/SHA256SUMS.txt
foreach ($line in $lines) {
    if ($line -notmatch '^([0-9a-f]{64})  ([A-Za-z0-9][A-Za-z0-9._-]*)$') { throw 'Malformed checksum file.' }
    $actual = (Get-FileHash (Join-Path verified-release $Matches[2]) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $Matches[1]) { throw "Checksum mismatch: $($Matches[2])" }
}
Get-AuthenticodeSignature verified-release/RobloxOne-win-x64-stable-Setup.exe |
    Format-List Status,StatusMessage,SignerCertificate
```

If any verification fails, do not replace assets in place. Keep or withdraw the
affected release as appropriate, investigate privately if security-sensitive,
fix forward with a new version, and publish a clear advisory when users may be
affected.
