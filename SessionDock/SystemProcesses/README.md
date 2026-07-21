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

HandleScope support is disabled by default. SessionDock does not include,
download, install, or elevate HandleScope. Regular users can opt in without
PowerShell:

1. Download and install HandleScope only from its
   [canonical repository](https://github.com/Makmatoe/HandleScope) or
   [release page](https://github.com/Makmatoe/HandleScope/releases).
2. Select **Integrations** in the SessionDock sidebar to open the HandleScope
   panel.
3. Select **Enable** to write the fixed, minimal per-user opt-in.
4. Select **Start API** to explicitly request a start of the API at its expected
   per-user installation path after local safety checks. SessionDock never
   starts it when the app or integration panel opens.
5. Select **Test connection** to check only its loopback health endpoint after
   the connection file and same-session process identity pass local checks.
   This test never enumerates or closes a handle.

HandleScope releases are not Authenticode-signed. SessionDock cannot prove the
publisher of an unsigned executable stored in a user-writable directory, so
install it only from the official release page and verify the published
checksum. The panel checks the exact standard path, rejects reparse points,
requires a structurally valid Windows executable, and checks the running
process path, session, owner, and non-elevated token before testing the
connection; these are local safety checks, not cryptographic publisher
verification.

The panel reports **Not installed**, **Installed - connection not tested**,
**API start requested**, **API running - connection not tested**,
**API running - integration disabled**, **Ready**, **Update required**, or a
configuration warning. A bounded start-pending state prevents rapid or
concurrent requests from spawning another API before discovery is published.
After that window, SessionDock verifies the process ID returned by the explicit
start request before it will allow another API process to be launched.
An invalid or nonminimal existing configuration is preserved. Only after
displaying that warning does the panel offer an explicit **Repair integration**
action, which replaces the SessionDock opt-in with the fixed minimal policy.
**Disable** prevents future SessionDock launch operations but does not stop the
HandleScope API.

Developers working from a SessionDock source checkout may instead run
`./scripts/Enable-HandleScope.ps1` from the repository root. HandleScope's
installed `Enable-SessionDockIntegration.ps1` helper provides the matching
command-line route. Both helpers and the UI use the same per-user opt-in; the
source helper does not install or start HandleScope.

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
same-session `HandleScope.Api` process at the exact expected executable path are
accepted. The process start time must also match the bounded discovery time so a
stale file or reused PID is rejected. The rotating token is used directly from
the HandleScope connection file and is not copied into SessionDock settings or
logs. If the file, API, token, policy, or selector is unavailable, the hook is
skipped.
