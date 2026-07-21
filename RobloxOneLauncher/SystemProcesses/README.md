# Optional local launch integrations

The integrations in this directory run only after Roblox Player starts
successfully. They are optional, loopback-only, bounded by short timeouts, and
cannot change a successful launch into a failed launch.

Roblox One waits for each bounded integration attempt before marking that step
finished. The activity panel distinguishes a configured attempt from a skipped
step, but it never reports an optional integration as the reason Roblox itself
did or did not launch.

## Generic local API hook

`LocalApiLaunchHook` sends one JSON `POST` when
`ROBLOX_ONE_LAUNCH_HOOK_URL` is an HTTP or HTTPS loopback URL. Redirects,
cookies, and system proxies are disabled.

Configure it for the current Windows user, then restart Roblox One:

```powershell
[Environment]::SetEnvironmentVariable(
    "ROBLOX_ONE_LAUNCH_HOOK_URL",
    "http://127.0.0.1:3000/roblox-launch",
    "User")
```

If the endpoint requires bearer authentication:

```powershell
[Environment]::SetEnvironmentVariable(
    "ROBLOX_ONE_LAUNCH_HOOK_BEARER_TOKEN",
    "replace-with-your-token",
    "User")
```

The payload contains an event ID and time, the launched PID, place ID,
experience name, public/private classification, and local account identity.
It deliberately excludes destinations, server codes, cookies, passwords,
authentication tickets, and WebView2 data.

## HandleScope connector

HandleScope support is disabled by default. Roblox One does not include or
launch HandleScope and never requests elevation for it. To opt in:

1. Download and install HandleScope only from its
   [canonical repository](https://github.com/Makmatoe/HandleScope) or
   [release page](https://github.com/Makmatoe/HandleScope/releases).
2. From the extracted HandleScope release, run its integration helper:

   ```powershell
   ./api/Enable-SessionDockIntegration.ps1
   ```

   This is the preferred path for release users. From a SessionDock source
   checkout, `./scripts/Enable-HandleScope.ps1` performs the equivalent
   per-user opt-in. Both scripts refuse to replace an existing configuration;
   review that file first, or deliberately pass `-Force` to reset it.
3. Start HandleScope's documented v1 local API. Roblox One never starts or
   elevates it.

The complete required configuration can be only:

```json
{"enabled": true}
```

Roblox One supplies the fixed v1 Roblox selector internally. Existing full
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
that was just launched. Roblox One first performs a dry run against that PID,
then closes only the matching handle. If `allProcesses` is enabled, it performs
one separately dry-run-checked sweep after the launched PID succeeds.

Each operation reloads `%LOCALAPPDATA%\HandleScope\connection.json`. Only an
exact v1 discovery document for `http://127.0.0.1:<port>/` and a live,
same-session `HandleScope.Api` process are accepted. The rotating token is used
directly from the HandleScope connection file and is not copied into Roblox One
settings or logs. If the file, API, token, policy, or selector is unavailable,
the hook is skipped.
