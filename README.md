# Roblox One

[![CI](https://github.com/Makmatoe/RobloxOne/actions/workflows/ci.yml/badge.svg)](https://github.com/Makmatoe/RobloxOne/actions/workflows/ci.yml)

Roblox One is a Windows launcher that keeps Roblox website sessions separate,
so you can choose the account and destination before opening Roblox Player.

[View Windows downloads](https://github.com/Makmatoe/RobloxOne/releases)

Release installers are published only on the canonical GitHub Releases page.
If that page has no release yet, a production installer is not currently
available. The project uses a no-cost release model: its update descriptor is
cryptographically signed, but its Windows executables are not Authenticode
code-signed and Windows may display **Unknown publisher** or a SmartScreen
warning.

> Roblox One is an independent project. It is not affiliated with, endorsed by,
> or sponsored by Roblox Corporation. Roblox and the Roblox logo are trademarks
> of Roblox Corporation.

## Quick start

1. Install Roblox Player. WebView2 is already included with Windows 11 and
   nearly all Windows 10 installations. Only if the sign-in view cannot open,
   install the [official Microsoft WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/consumer/).
2. Open the canonical [GitHub Releases](https://github.com/Makmatoe/RobloxOne/releases)
   page and download both the Setup executable and its `SHA256SUMS.txt` asset
   from the latest release. The checksum file also has a stable
   [latest-release download link](https://github.com/Makmatoe/RobloxOne/releases/latest/download/SHA256SUMS.txt).
3. Confirm the download came from `Makmatoe/RobloxOne`, then use the
   [regular-user checksum commands](docs/UPDATES.md#verify-a-manual-installer-download)
   before running it. If Windows warns about the unknown publisher, continue
   only when the filename and SHA-256 match that same release.
4. Add an account, then sign in on the official Roblox page shown in its
   isolated browser session.
5. Choose a destination and select **Launch Roblox**.

The installer is the recommended edition and supports in-app updates. A
portable ZIP is also published for temporary use, but it does not update
itself.

## What it does

- Keeps any number of Roblox sign-ins in separate local WebView2 profiles.
- Gives accounts custom labels and colors and remembers a destination per
  account.
- Opens public places, official private-server links or codes, and supported
  server IDs recovered from recent launches.
- Shares Recent and Favorites across accounts while preserving the account and
  public/private context of each launch.
- Launches one account at a time or runs a best-effort sequential batch with a
  configurable delay. Batch mode verifies selected sign-ins before closing any
  running clients, can be cancelled, and restores the previously selected
  account. Roblox still decides whether multiple Players may run.
- Closes all visible and background Roblox Player processes on request.
- Provides optional interface sounds and a user-selected startup sound.

## Local by design

Roblox One has no cloud backend, advertising, or telemetry. It does not ask
for, read, or store Roblox passwords. Account browser profiles, settings,
favorites, and recent-launch metadata remain under `%LOCALAPPDATA%\RobloxOne`.

Roblox One's direct Roblox API requests and top-level sign-in navigation are
limited to official Roblox HTTPS endpoints. Embedded Roblox pages may still
load subresources chosen by Roblox. The Player executable is location-checked
and Windows-signature-checked before launch.
Optional post-launch integrations accept loopback addresses only and are off
until the user configures them. HandleScope is never bundled, installed,
elevated, or started by Roblox One.

To use the optional connector, install
[HandleScope from its canonical repository](https://github.com/Makmatoe/HandleScope),
then run its installed helper from a normal, non-administrator PowerShell:

```powershell
& "$env:LOCALAPPDATA\Programs\HandleScope\Api\Enable-SessionDockIntegration.ps1"
```

Start HandleScope's documented v1 local API separately; Roblox One never
starts it. The installed path above is the canonical route for release users.
Developers working from a SessionDock source checkout may instead run
`./scripts/Enable-HandleScope.ps1` from the repository root to write the same
per-user opt-in; that source-only fallback does not install or start
HandleScope. SessionDock stores only `{"enabled":true}`; its narrow Roblox
handle policy remains compiled into the app.

The embedded sign-in view intentionally does not load extensions or password
manager integrations. It supports normal clipboard paste and its context menu,
while keeping each Roblox account in its own isolated local browser profile.

Read [Privacy](docs/PRIVACY.md) for the complete data/network summary and
[Security](SECURITY.md) before reporting a security issue.

## Updates

The top-right update button checks this repository's stable GitHub Releases
feed. Roblox One shows the version and cryptographically signed release notes
before it downloads anything, and installs only after confirmation.

Production updates require a release descriptor authorized by the public key
pinned in the app. The descriptor binds the version, notes, exact package name,
size, and SHA-256; the app then enforces an exact package-content allowlist.
The free release pipeline does not claim Windows publisher identity. Source,
debug, raw publish, and portable builds cannot replace themselves. See
[Updates](docs/UPDATES.md) for the regular-user flow.

## Build and verify

The repository pins its .NET SDK, dependencies, and packaging tool. From the
repository root:

```powershell
dotnet restore --locked-mode
./scripts/Build.ps1 -Configuration Release -Runtime win-x64 -CI
```

To run the desktop project during development:

```powershell
dotnet run --project ./RobloxOneLauncher/RobloxOneLauncher.csproj
```

Local builds are development artifacts, not official Roblox One releases.
Release packages include the MIT license, pinned upstream licenses and notices,
an SPDX SBOM, checksums, and GitHub artifact attestations.
Maintainer setup and the tag-driven release checklist are in
[Releasing](docs/RELEASING.md). Optional local hook configuration is documented
under [SystemProcesses](RobloxOneLauncher/SystemProcesses/README.md).

## License and contributions

Roblox One is open source under the [MIT License](LICENSE.md). Read
[CONTRIBUTING.md](CONTRIBUTING.md) before proposing code changes.
