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

1. Install and configure HandleScope separately.
2. Start its official local API by the method documented by HandleScope.
3. Copy `handlescope.example.json` to
   `%LOCALAPPDATA%\RobloxOne\handlescope.json`.
4. Set `enabled` to `true` and replace the selector fields with the exact values
   supplied by HandleScope.

Example:

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

Each operation reloads `%LOCALAPPDATA%\HandleScope\connection.json`. The URL
must resolve to the local machine. The rotating token is used directly from the
HandleScope connection file and is not copied into Roblox One settings or logs.
If the file, API, token, or selector is unavailable, the hook is skipped.
