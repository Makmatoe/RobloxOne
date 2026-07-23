# SessionDock desktop project

This directory contains the Windows WPF application. Repository-level build,
security, privacy, and release instructions are in the [root README](../README.md).

## Development run

From the repository root:

```powershell
dotnet run --project .\SessionDock\SessionDock.csproj
```

Development and raw `dotnet publish` builds are intentionally not self-updating.
Only a Velopack Setup installation from the canonical GitHub Releases page
enables the production update path. Release executables are intentionally not
Authenticode code-signed; the updater instead requires the independently
signed release descriptor and exact package integrity checks.

## Local data

SessionDock keeps account-slot metadata, launch history, preferences, imported
sounds, and isolated WebView2 profiles under `%LOCALAPPDATA%\SessionDock`. No
account data, cookies, passwords, tokens, or private-server codes are compiled
into the application.

The app is single-instance for each Windows login session. Each saved account
has its own WebView2 profile. Roblox credentials are entered only on official
Roblox pages and are not read or stored by SessionDock.

## Main components

- `Services/DestinationParser.cs` validates supported Roblox destinations.
- `Services/JoinUserDestination.cs` validates explicit user destinations so a
  numeric user ID cannot be confused with a Place ID.
- `Services/RobloxWebSessionService.cs` manages isolated browser sessions.
- `Services/RobloxClientService.cs` discovers, verifies, launches, and closes
  Roblox Player processes.
- `Services/SessionDockUpdateService.cs` coordinates the manual Velopack updater and
  requires a descriptor authorized by the pinned release key.
- `SystemProcesses/` contains optional, loopback-only post-launch connectors.
- `MainWindow.*.cs` splits UI coordination by launcher feature.

## Optional integrations

SessionDock can notify a user-configured loopback endpoint after a successful
launch. It can also use a separately installed HandleScope local API when the
user explicitly enables the fixed Roblox policy. SessionDock never bundles,
downloads, installs, or elevates HandleScope. Its integration panel can start
only the API at the expected per-user installation path after local safety
checks, and only after the user explicitly selects **Start API**. See
[SystemProcesses/README.md](SystemProcesses/README.md).

## Updates

The top-right update button checks the canonical stable GitHub release feed.
The app verifies the signed release descriptor before it displays notes or
downloads a package. Velopack then verifies and stages the authorized package;
installation happens only after the user confirms and SessionDock exits.

Release engineering details are in [docs/RELEASING.md](../docs/RELEASING.md).
