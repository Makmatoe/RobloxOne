# Maintainer release guide

Roblox One uses a no-cost, tag-triggered Windows release pipeline. It combines
an independently ECDSA-signed release descriptor, exact SHA-256/package
validation, GitHub's protected release environment, immutable GitHub Releases,
and GitHub artifact attestations. The Windows executables are intentionally not
Authenticode code-signed, so the project does not claim a verified Windows
publisher and users may see **Unknown publisher** or SmartScreen warnings.

## One-time repository configuration

Keep these controls enabled before publishing:

1. The canonical repository must be public so the tokenless updater can read
   its releases.
2. Protect `main` with pull requests, passing checks, resolved conversations,
   linear history, and no force pushes or deletion.
3. Protect tags matching `v*` from updates and deletion.
4. Keep GitHub Actions' default token read-only. The release workflow grants
   write permissions only to its protected publication job.
5. Keep private vulnerability reporting, Dependabot alerts, secret scanning,
   push protection, CodeQL, and immutable releases enabled where available.
6. Keep the `release` environment approval-protected and limited to tags
   matching `v*`.
7. Store the single-line base64 of the P-256 PKCS#8 private key matching
   `RobloxOneLauncher/Resources/update-public-key.pem` only in the protected
   environment secret `UPDATE_SIGNING_PRIVATE_KEY_PKCS8_BASE64`. Never commit, print,
   upload as an artifact, or copy that private key into application data.

There are no Azure, certificate-authority, or paid-signing requirements. The
descriptor key is the only protected release secret. If it is lost, publish a
new application version with a deliberately rotated embedded public key through
normal review; an older installation cannot safely trust that new key without a
manual reinstall.

## Prepare a release

1. Start from a clean, reviewed `main` commit.
2. Choose an unreleased semantic version in `major.minor.patch` form.
3. Set the project version to exactly that value.
4. Add `RobloxOneLauncher/ReleaseNotes/<version>.md`. Keep notes user-focused,
   displayable, and free of secrets or untrusted HTML.
5. Restore, build, test, and run repository validation locally.
6. Confirm the publish inventory contains only the application, MIT license,
   dependency notices, and pinned upstream license files.
7. Review dependency vulnerability output, the SPDX SBOM, and the complete
   release diff.
8. Confirm no release or draft already exists for the version.
9. Merge through the protected branch after required checks pass.

The project version, notes filename, tag, package version, descriptor version,
and Velopack version must agree. Every mismatch fails closed.

## Publish

Create and push an annotated version tag only after the reviewed commit is the
tip of `main`:

```powershell
$version = Read-Host 'New release version without the v prefix (major.minor.patch)'
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a major.minor.patch version.' }
$tag = "v$version"
$notesPath = "RobloxOneLauncher/ReleaseNotes/$version.md"
if (-not (Test-Path -LiteralPath $notesPath -PathType Leaf)) {
    throw "Missing release notes: $notesPath"
}

git switch main
git pull --ff-only
git tag -a $tag -m "Roblox One $version"
git push origin $tag
```

The protected workflow then:

1. verifies that the annotated tag is the current protected `main` tip;
2. restores locked dependencies and runs the full Release build/test suite;
3. publishes the self-contained Windows x64 application;
4. uses the exact pinned Velopack CLI to create Setup, full-package, and
   portable assets without Authenticode signing;
5. signs a bounded descriptor containing the final package filename, size,
   SHA-256, channel, key ID, version, timestamp, and release notes;
6. verifies that signature with the public key embedded in the application;
7. enforces exact release, package, portable-ZIP, license, and feed inventories;
8. publishes an SPDX 2.3 SBOM and `SHA256SUMS.txt` covering every other asset;
9. creates a fresh draft, uploads it, downloads every asset again, and compares
   exact hashes;
10. creates GitHub artifact attestations for those downloaded assets; and
11. publishes the immutable release only after all validation succeeds.

Actions are pinned to full commit SHAs. The workflow is tag-only, does not use
`pull_request_target`, does not clobber assets, and exposes the descriptor key
only to its one signing step. Never move or reuse a published version tag. If a
release has been published, fix forward with a new version.

## Verify after publication

Before announcing a release:

- install Setup on a clean Windows x64 test account or VM;
- expect Windows to report an unknown publisher, and verify that no README or
  dialog claims otherwise;
- confirm the installed version and exercise add/remove account, destination
  parsing, single launch, Recent, cancellation, and optional integrations;
- confirm the top-right update button reports no newer version;
- from the preceding installed version, verify update discovery, signed notes,
  download, restart, retained local data, and final version; and
- verify the checksums and GitHub attestation for every asset.

```powershell
$tag = Read-Host 'Published release tag (v followed by major.minor.patch)'
if ($tag -notmatch '^v\d+\.\d+\.\d+$') { throw 'Expected a vmajor.minor.patch tag.' }
$directory = "verified-release-$tag"

gh release download $tag --repo Makmatoe/RobloxOne --dir $directory
if ($LASTEXITCODE -ne 0) { throw "Release download failed: $tag" }
Get-ChildItem -LiteralPath $directory -File | ForEach-Object {
    gh attestation verify $_.FullName --repo Makmatoe/RobloxOne
    if ($LASTEXITCODE -ne 0) { throw "Attestation verification failed: $($_.Name)" }
}
$lines = Get-Content (Join-Path $directory 'SHA256SUMS.txt')
foreach ($line in $lines) {
    if ($line -notmatch '^([0-9a-f]{64})  ([A-Za-z0-9][A-Za-z0-9._-]*)$') { throw 'Malformed checksum file.' }
    $actual = (Get-FileHash (Join-Path $directory $Matches[2]) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $Matches[1]) { throw "Checksum mismatch: $($Matches[2])" }
}
Get-AuthenticodeSignature (Join-Path $directory 'RobloxOne-win-x64-stable-Setup.exe') |
    Format-List Status,StatusMessage
```

For this no-cost model the final command is expected to report `NotSigned`.
That is a disclosed distribution limitation, not a successful publisher check.
If any descriptor, checksum, inventory, or attestation verification fails, do
not run or replace assets; investigate and publish a new version.
