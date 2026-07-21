# Contributing

Thank you for helping improve Roblox One. Bug reports, usability feedback, and
focused feature proposals are welcome.

## Before submitting code

This repository is source-visible and does not grant an open-source license.
External code contributions are accepted only after the maintainer has agreed
in writing to the scope and contribution terms. Open an issue before doing
substantial implementation work. An unsolicited pull request may be closed
without review.

Never submit Roblox credentials, cookies, launch tickets, private-server codes,
local account data, signing keys, access tokens, HandleScope connection files,
or production certificates.

## Development

Roblox One targets Windows x64 and .NET 10. Use the SDK selected by
`global.json` and work from a short-lived branch based on `main`.

```powershell
dotnet restore --locked-mode
./scripts/Build.ps1 -Configuration Release -Runtime win-x64 -CI
```

Keep changes narrowly scoped. Preserve the project's local-first behavior and
do not add network destinations, telemetry, automatic updates, elevated helper
processes, or third-party packages without an explicit security and maintenance
case. Prefer .NET and Windows platform APIs. Roblox network calls must use
official Roblox endpoints.

## Pull requests

An approved pull request should:

- explain the user-visible behavior and security impact;
- include tests for parsers, trust decisions, update metadata, or persistence
  logic when those areas change;
- keep account and browser-profile data out of fixtures and screenshots;
- update user and maintainer documentation when behavior changes;
- pass formatting, build, test, dependency, and secret-scanning checks; and
- avoid generated build output, local settings, or release secrets.

Do not edit a release tag after publication. Release preparation and signing
follow [docs/RELEASING.md](docs/RELEASING.md).

## Reporting security issues

Follow [SECURITY.md](SECURITY.md). Security issues must not be discussed in a
public issue or pull request until the maintainer confirms disclosure is safe.

## Conduct

Participation in this repository is subject to
[CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
