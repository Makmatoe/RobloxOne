# Updates for regular users

Roblox One uses a manual, one-click updater built with Velopack and backed by
the canonical project's GitHub Releases. Updates are not silently installed.

## Normal update flow

1. Select the top-right update button in Roblox One.
2. The app contacts only the canonical Roblox One
   GitHub release feed.
3. If a newer stable version exists, review its version and signed release
   notes.
4. Confirm installation. Cancelling leaves the current version unchanged.
5. Roblox One downloads the authorized package, checks its signed SHA-256,
   exact contents, and version, closes, and asks Velopack to replace and reopen
   the application.

If a verified package was downloaded during an earlier attempt, the update
button asks whether to restart and install that pending version.

The installer-based production build is the recommended updateable edition.
Source, debug, and raw `dotnet publish` builds do not become trusted production
installations and cannot use the production self-update path.

## What is verified

The updater requires a signed release descriptor whose public key is pinned in
the application. That descriptor authorizes the exact target version, package
filename, size, SHA-256 digest, channel, and release notes. Velopack also checks
the downloaded package against the authorized package metadata.

The project intentionally uses a free release model. Its Windows executables
are not Authenticode code-signed, so Windows may display **Unknown publisher**
or a SmartScreen warning. The signed descriptor protects the in-app release
decision independently of GitHub asset metadata. GitHub artifact attestations
and published checksums provide additional, manually verifiable provenance.

Immediately before scheduling installation, Roblox One rechecks the downloaded
full package against the signed size and SHA-256, extracts only its application
executables into a locked temporary directory, rejects missing, duplicate,
path-like, oversized, or unexpected archive entries, validates the package
identity/version metadata, and checks that each expected executable is
structurally a Windows PE file. This confirms exact signed-package integrity;
it does not establish a certificate-backed Windows publisher identity.

Release notes are displayed as bounded, inert text. Web content from a release
is not executed in the application.

Each release also publishes an SPDX SBOM, complete bundled dependency notices,
SHA-256 checksums for every other asset, and GitHub attestations. These aid
independent inspection; the in-app trust decision relies on the signed
descriptor, exact package hash, and package allowlist.

## User data

Application files and local user data are separate. An update replaces the
application, not the data under `%LOCALAPPDATA%\RobloxOne`, so account slots,
isolated WebView2 profiles, favorites, recent history, labels, colors, and sound
preferences normally remain in place.

Updates never contain another user's account profiles or settings. Removing
Roblox One does not imply that Roblox or WebView2 data was removed; use the
application's account removal controls first when profile deletion is desired.

## If an update fails

- Keep the existing version open if the check, signature verification, or
  download fails.
- Retry from a stable network, then check the canonical
  [release page](https://github.com/Makmatoe/RobloxOne/releases) for notices.
- Do not download a replacement executable from chat, email, file-sharing, or
  a repository fork.
- Windows is expected to report an unknown publisher because the project does
  not buy an Authenticode certificate. Continue only for assets from the
  canonical release page whose published checksum matches. Treat any claim
  that Roblox One currently has a verified Windows publisher as suspicious.
