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
   available.
6. Create a protected `release` environment with a required human reviewer and
   prevention of self-review. Only the final signing/publishing job may use it.
7. Configure Azure workload identity federation for GitHub Actions. Do not
   store a long-lived Azure client secret.

The production workflow expects these protected environment variables:

- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`
- `AZURE_SIGNING_ENDPOINT`
- `AZURE_SIGNING_ACCOUNT`
- `AZURE_SIGNING_PROFILE`
- `EXPECTED_PUBLISHER_SUBJECT`

It also expects `UPDATE_SIGNING_PRIVATE_KEY_PEM` as a protected environment
secret. This key signs only the bounded release descriptor. Limit environment
access, rotate the key deliberately with a new embedded key identifier, and
never print, upload, cache, or persist it. A managed signing service should
replace the repository secret if one becomes available for this descriptor
format.

## Prepare a release

1. Start from a clean, reviewed `main` commit.
2. Choose a semantic version such as `2.1.0`.
3. Set the project version to exactly that value.
4. Add `ReleaseNotes/2.1.0.md`. Keep notes user-focused, displayable, and free
   of secrets or untrusted HTML.
5. Restore, build, test, and run the repository validation scripts locally.
6. Review dependency vulnerability output and the complete release diff.
7. Merge through the protected branch after required checks pass.

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
8. create GitHub artifact attestations for final downloadable assets; and
9. publish the immutable release only after all verification succeeds.

Actions must be pinned to full commit SHAs. Release jobs must not execute code
from an untrusted pull request, use `pull_request_target`, or expose the signing
environment to arbitrary workflow inputs. Never rerun a release for an existing
version by moving its tag; issue a new version instead.

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

If any verification fails, do not replace assets in place. Keep or withdraw the
affected release as appropriate, investigate privately if security-sensitive,
fix forward with a new version, and publish a clear advisory when users may be
affected.
