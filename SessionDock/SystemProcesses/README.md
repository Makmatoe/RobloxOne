# Optional local launch integrations

The integrations in this directory run only after Roblox Player starts
successfully. They are optional, loopback-only, bounded by short timeouts, and
cannot change a successful launch into a failed launch.

SessionDock waits for each bounded integration attempt before marking that step
finished. The activity panel distinguishes a configured attempt from a skipped
step, but it never reports an optional integration as the reason Roblox itself
did or did not launch.

## Generic local API hook

`LocalApiLaunchHook` sends one JSON `POST` when
`SESSIONDOCK_LAUNCH_HOOK_URL` is an HTTP or HTTPS loopback URL. Redirects,
cookies, and system proxies are disabled.

Configure it for the current Windows user, then restart SessionDock:

```powershell
[Environment]::SetEnvironmentVariable(
    "SESSIONDOCK_LAUNCH_HOOK_URL",
    "http://127.0.0.1:3000/roblox-launch",
    "User")
```

If the endpoint requires bearer authentication:

```powershell
[Environment]::SetEnvironmentVariable(
    "SESSIONDOCK_LAUNCH_HOOK_BEARER_TOKEN",
    "replace-with-your-token",
    "User")
```

The payload contains an event ID and time, the launched PID, place ID,
experience name, public/private classification, and local account identity.
It deliberately excludes destinations, server codes, cookies, passwords,
authentication tickets, and WebView2 data.

## HandleScope connector

HandleScope support is disabled by default. SessionDock does not include or
launch HandleScope and never requests elevation for it. To opt in:

1. Download and install HandleScope only from its
   [canonical repository](https://github.com/Makmatoe/HandleScope) or
   [release page](https://github.com/Makmatoe/HandleScope/releases).
2. After installing HandleScope's API, run its installed integration helper
   from a normal, non-administrator PowerShell:

   ```powershell
   & "$env:LOCALAPPDATA\Programs\HandleScope\Api\Enable-SessionDockIntegration.ps1"
   ```

   This installed path is the canonical route for release users. Developers
   working from a SessionDock source checkout may instead run
   `./scripts/Enable-HandleScope.ps1` from the SessionDock repository root to
   write the same per-user opt-in. That source-only fallback does not install
   or start HandleScope. Both helpers refuse to replace an existing
   configuration; review that file first, or deliberately pass `-Force` to
   reset it.
3. Start HandleScope's documented v1 local API. SessionDock never starts or
   elevates it.

The complete required configuration can be only:

```json
{"enabled": true}
```

SessionDock supplies the fixed v1 Roblox selector internally. Existing full
configuration files remain supported, but any explicitly supplied selector
that differs from the fixed policy disables the integration rather than
broadening it.

Full compatibility example:

```json
{
  "enabled": true,
  "processName": "RobloxPlayerBeta",
  "handleName": "\\Sessions\\{SESSION_ID}\\BaseNamedObjects\\ROBLOX_singletonEvent",
  "handleType": "Event",
  "access": "0x001F0003",
  "match": "exact",
  "closeAll": false,
  "allProcesses": true,
  "retryTimeoutSeconds": 10,
  "retryIntervalMilliseconds": 500
}
```

`{SESSION_ID}` is replaced with the Windows session ID of the exact Roblox PID
that was just launched. SessionDock first performs a dry run against that PID,
then closes only the matching handle. If `allProcesses` is enabled, it performs
one separately dry-run-checked sweep after the launched PID succeeds.

Each operation reloads `%LOCALAPPDATA%\HandleScope\connection.json`. Only an
exact v1 discovery document for `http://127.0.0.1:<port>/` and a live,
same-session `HandleScope.Api` process are accepted. The rotating token is used
directly from the HandleScope connection file and is not copied into SessionDock
settings or logs. If the file, API, token, policy, or selector is unavailable,
the hook is skipped.
