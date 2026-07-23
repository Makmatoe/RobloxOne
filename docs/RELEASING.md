# Maintainer release guide

SessionDock uses a no-cost, tag-triggered Windows release pipeline. It combines
an independently ECDSA-signed release descriptor, exact SHA-256/package
validation, GitHub's protected release environment, immutable GitHub Releases,
GitHub artifact attestations, and a separate final-publication approval. The
Windows executables are intentionally not
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
   write permissions only to its two bounded protected jobs.
5. Keep private vulnerability reporting, Dependabot alerts, secret scanning,
   push protection, CodeQL, and immutable releases enabled where available.
6. Create a `release` environment for signing and staging the verified draft.
   Require a reviewer, select **Selected branches and tags**, add only the
   deployment tag rule `v*`, and disable administrator bypass. Store the
   single-line base64 of the P-256 PKCS#8 private key matching
   `SessionDock/Resources/update-public-key.pem` only in this environment's
   secret `UPDATE_SIGNING_PRIVATE_KEY_PKCS8_BASE64`.
7. Create a separate `release-publication` environment for the final approval.
   Require a reviewer, select **Selected branches and tags**, add only the
   deployment tag rule `v*`, and disable administrator bypass. Do not add any
   secrets to this environment. The final job receives only release-write and
   attestation-read permissions, and it starts only after a verified draft
   exists.
8. When the repository has only one maintainer able to review deployments,
   leave **Prevent self-review** disabled or the release will deadlock. When a
   second trusted maintainer is available, enable it and require that person to
   approve both environments.

Never commit, print, upload as an artifact, or copy the descriptor private key
into application data.

There are no Azure, certificate-authority, or paid-signing requirements. The
descriptor key is the only protected release secret. If it is lost, publish a
new application version with a deliberately rotated embedded public key through
normal review; an older installation cannot safely trust that new key without a
manual reinstall.

## Prepare a release

1. Start from a clean, reviewed `main` commit.
2. Choose an unreleased semantic version in `major.minor.patch` form.
3. Set the project version to exactly that value.
4. Add `SessionDock/ReleaseNotes/<version>.md`. Keep notes user-focused,
   displayable, and free of secrets or untrusted HTML.
5. Restore, build, test, and run repository validation locally.
6. Publish and execute the isolated runtime smoke before tagging:

   ```powershell
   ./scripts/Build.ps1 -Configuration Release -Runtime win-x64 `
       -OutputDirectory artifacts/runtime-smoke -CI
   ./scripts/Test-RuntimeSmoke.ps1
   ```

   The smoke starts the published executable hidden with a unique, previously
   nonexistent directory directly under the current user's temporary folder.
   It never uses the normal SessionDock or legacy RobloxOne data roots. It
   requires a clean signed-out startup, isolated settings and sound storage,
   the production window-closing path, and a zero exit code within 20 seconds,
   then removes only that validated temporary directory.
7. Confirm the publish inventory contains only the application, MIT license,
   dependency notices, and pinned upstream license files.
8. Review dependency vulnerability output, the SPDX SBOM, and the complete
   release diff.
9. Confirm no release or draft already exists for the version.
10. Merge through the protected branch after required checks pass.

The project version, notes filename, tag, package version, descriptor version,
and Velopack version must agree. Every mismatch fails closed.

The Velopack package ID is `SessionDockApp`. It must never equal the current
data directory name `SessionDock` or the historic combined install/data name
`RobloxOne`. Changing this invariant is a data-loss-sensitive release change.

Full update packages intentionally omit Velopack `runtimeDependencies` metadata.
The 2.4.0 updater validates an exact metadata set, and existing installations
can update directly to any later release. Adding `--framework webview2` would
make those safe updates fail closed. SessionDock therefore contains WebView2
startup failures in the application and directs users to Microsoft's fixed
official repair page. Do not add runtime dependency metadata until every
supported updater can accept it without requiring an intermediate release.

## Publish

Create and push an annotated version tag only after the reviewed commit is the
tip of `main`:

```powershell
$version = Read-Host 'New release version without the v prefix (major.minor.patch)'
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a major.minor.patch version.' }
$tag = "v$version"
$notesPath = "SessionDock/ReleaseNotes/$version.md"
if (-not (Test-Path -LiteralPath $notesPath -PathType Leaf)) {
    throw "Missing release notes: $notesPath"
}

git switch main
git pull --ff-only
git tag -a $tag -m "SessionDock $version"
git push origin $tag
```

The protected workflow then:

1. verifies that the annotated tag is the current protected `main` tip;
2. restores locked dependencies and runs the full Release build/test suite;
3. publishes the self-contained Windows x64 application;
4. runs `Test-RuntimeSmoke.ps1` against that exact published executable in an
   isolated, disposable data root;
5. uses the exact pinned Velopack CLI to create Setup, full-package, and
   portable assets without Authenticode signing;
6. signs a bounded descriptor containing the final package filename, size,
   SHA-256, channel, key ID, version, timestamp, and release notes;
7. verifies that signature with the public key embedded in the application;
8. enforces exact release, package, portable-ZIP, license, and feed inventories;
9. publishes an SPDX 2.3 SBOM and `SHA256SUMS.txt` covering every other asset;
10. creates a fresh draft, uploads it, downloads every asset again, and compares
   exact hashes;
11. creates GitHub artifact attestations for those downloaded assets;
12. waits at the `release-publication` environment for explicit acceptance;
13. after approval, downloads the draft again and enforces its exact inventory,
    ordinal filename/checksum matching, release identity, and GitHub
    attestations bound to the canonical release workflow, tag ref, and commit;
    and
14. publishes the reverified draft as the immutable latest release.

Actions are pinned to full commit SHAs. The workflow is tag-only, does not use
`pull_request_target`, does not clobber assets, and exposes the descriptor key
only to its one signing step. Never move or reuse a published version tag. If a
release has been published, fix forward with a new version.

## Verify the draft before publication

Wait for `Sign, attest, and stage verified draft` to succeed and for `Approve
and publish verified release` to show that it is waiting for the
`release-publication` environment. The draft is not visible to the tokenless
updater, so authenticate `gh` as a repository maintainer and download it by tag:

```powershell
$tag = Read-Host 'Draft release tag (v followed by major.minor.patch)'
if ($tag -notmatch '^v\d+\.\d+\.\d+$') { throw 'Expected a vmajor.minor.patch tag.' }
$directory = "verified-draft-$tag"

gh release download $tag --repo Makmatoe/SessionDock --dir $directory
if ($LASTEXITCODE -ne 0) { throw "Draft download failed: $tag" }
Get-ChildItem -LiteralPath $directory -File | ForEach-Object {
    gh attestation verify $_.FullName --repo Makmatoe/SessionDock
    if ($LASTEXITCODE -ne 0) { throw "Attestation verification failed: $($_.Name)" }
}
$lines = Get-Content (Join-Path $directory 'SHA256SUMS.txt')
foreach ($line in $lines) {
    if ($line -notmatch '^([0-9a-f]{64})  ([A-Za-z0-9][A-Za-z0-9._-]*)$') { throw 'Malformed checksum file.' }
    $actual = (Get-FileHash (Join-Path $directory $Matches[2]) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $Matches[1]) { throw "Checksum mismatch: $($Matches[2])" }
}
Get-AuthenticodeSignature (Join-Path $directory 'SessionDock-win-x64-Setup.exe') |
    Format-List Status,StatusMessage
```

For this no-cost model the final command is expected to report `NotSigned`.
That is a disclosed distribution limitation, not a successful publisher check.

Before approving publication:

- install Setup on a clean Windows x64 test account or VM;
- expect Windows to report an unknown publisher, and verify that no README or
  dialog claims otherwise;
- confirm the installed version and exercise add/remove account, destination
  parsing, single launch, Recent, cancellation, and optional integrations;
- confirm restart recovery and retained disposable test data;
- for the 2.3.1 corrective boundary only, install side-by-side with a disposable
  Roblox One 2.1.4/SessionDock 2.3.0 fixture whose legacy root contains
  `current`, `packages`, `Update.exe`, settings, and browser profiles; verify
  that only allowlisted user data is copied, both accounts remain visible, and
  every legacy source and installer file remains byte-identical; and
- approve `release-publication` only when all draft checks pass.

If a draft fails, do not approve it. Investigate first; remove only that failed,
unpublished draft before rerunning the same protected tag workflow. Never move
or reuse a published tag.

## Verify after publication

Before announcing a release:

- confirm the top-right update button reports no newer version;
- from the preceding installed version, verify update discovery, signed notes,
  download, restart, retained disposable test data, and final version, except at the
  deliberate 2.3.0-to-2.3.1 package-ID boundary, which must use the
  side-by-side corrective-install test above; and
- confirm the public release is immutable and its asset inventory still matches
  the verified draft.

If any descriptor, checksum, inventory, or attestation verification fails, do
not run or replace assets; investigate and publish a new version.
