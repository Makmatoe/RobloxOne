# Roblox One

[![CI](https://github.com/Makmatoe/RobloxOne/actions/workflows/ci.yml/badge.svg)](https://github.com/Makmatoe/RobloxOne/actions/workflows/ci.yml)

Roblox One is a Windows launcher that keeps Roblox website sessions separate,
so you can choose the account and destination before opening Roblox Player.

[Download the signed Windows installer](https://github.com/Makmatoe/RobloxOne/releases/latest/download/RobloxOne-win-x64-stable-Setup.exe)

> Roblox One is an independent project. It is not affiliated with, endorsed by,
> or sponsored by Roblox Corporation. Roblox and the Roblox logo are trademarks
> of Roblox Corporation.

## Quick start

1. Install Roblox Player and the Microsoft Edge WebView2 Runtime.
2. Download `RobloxOne-win-x64-stable-Setup.exe` from the canonical
   [GitHub Releases](https://github.com/Makmatoe/RobloxOne/releases) page.
3. Add an account, then sign in on the official Roblox page shown in its
   isolated browser session.
4. Choose a destination and select **Launch Roblox**.

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
  configurable delay. Roblox still decides whether multiple Players may run.
- Closes all visible and background Roblox Player processes on request.
- Provides optional interface sounds and a user-selected startup sound.

## Local by design

Roblox One has no cloud backend, advertising, or telemetry. It does not ask
for, read, or store Roblox passwords. Account browser profiles, settings,
favorites, and recent-launch metadata remain under `%LOCALAPPDATA%\RobloxOne`.

Roblox web traffic is limited to official Roblox HTTPS endpoints. The Player
executable is location-checked and Windows-signature-checked before launch.
Optional post-launch integrations accept loopback addresses only and are off
until the user configures them. HandleScope is never bundled, installed,
elevated, or started by Roblox One.

Read [Privacy](docs/PRIVACY.md) for the complete data/network summary and
[Security](SECURITY.md) before reporting a security issue.

## Updates

The top-right update button checks this repository's stable GitHub Releases
feed. Roblox One shows the version and signed release notes before it downloads
anything, and installs only after confirmation.

Production updates require both a release descriptor authorized by the public
key pinned in the app and an Authenticode-signed Windows application from the
same publisher as the installed copy. Source, debug, raw publish, and portable
builds cannot replace themselves. See [Updates](docs/UPDATES.md) for the
regular-user flow.

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
Maintainer setup and the tag-driven release checklist are in
[Releasing](docs/RELEASING.md). Optional local hook configuration is documented
under [SystemProcesses](RobloxOneLauncher/SystemProcesses/README.md).

## License and contributions

This repository is source-visible, not open source. No permission to use,
copy, modify, or redistribute the code is granted without written permission
from the copyright holder; see [LICENSE.md](LICENSE.md). Read
[CONTRIBUTING.md](CONTRIBUTING.md) before proposing code changes.
