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
5. Roblox One downloads the authorized package, checks its SHA-256 and Windows
   publisher, closes, and asks Velopack to replace and reopen the application.

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

Production application files are Authenticode-signed through Azure Artifact
Signing. Windows signature verification identifies the expected publisher; the
independent descriptor protects the in-app release decision. GitHub artifact
attestations provide additional build provenance but do not replace either
signature check.

Immediately before scheduling installation, Roblox One rechecks the downloaded
full package against the signed size and SHA-256, extracts only its application
executable into a locked temporary file, validates the Windows trust chain and
version, and requires its signer subject to match the currently installed,
trusted Roblox One executable. A deliberate publisher-identity change therefore
requires a fresh Setup installation instead of silently crossing that boundary.

Release notes are displayed as bounded, inert text. Web content from a release
is not executed in the application.

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
- If Windows reports an unexpected or missing publisher signature on a claimed
  production release, do not run it. Report it privately under
  [SECURITY.md](../SECURITY.md).
