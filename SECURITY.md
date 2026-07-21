# Security policy

## Supported versions

Only the latest production release published from the canonical
[Roblox One repository](https://github.com/Makmatoe/RobloxOne/releases) is
supported. Development builds, portable test artifacts, and older releases may
not receive security fixes.

## Reporting a vulnerability

Do not open a public issue for a suspected vulnerability or include secrets,
tokens, cookies, private-server codes, personal data, or exploit details in an
issue or pull request.

Use GitHub's **Report a vulnerability** private-reporting feature on the
repository's Security page. Include:

- the affected version and Windows version;
- a concise description of the impact and security boundary crossed;
- reproducible steps or a minimal proof of concept;
- whether Roblox account, local-file, process, update, or code-execution data is
  involved; and
- any suggested mitigation.

If private vulnerability reporting is temporarily unavailable, open a minimal
public issue that asks the maintainer to establish a private contact channel.
Do not disclose the vulnerability in that issue.

No bug bounty, payment, or response deadline is promised. Good-faith reports
will be reviewed as capacity permits. Do not access other people's accounts or
data, degrade Roblox or GitHub services, run denial-of-service testing, or use
social engineering while researching Roblox One.

## Security boundaries

Roblox One is designed around these boundaries:

- Roblox credentials and cookies belong to isolated WebView2 profiles and are
  not application configuration data.
- Launch tickets are short-lived values used for process launch and must not be
  logged or persisted.
- Only trusted Roblox installation paths and Roblox-signed Player executables
  may be launched or closed.
- Application updates come only from this repository and require a valid
  descriptor signed by the release key pinned in the app, an exact package
  hash, bounded metadata, and an exact package-content allowlist.
- Optional HTTP hooks and HandleScope communication are loopback-only. The
  HandleScope API is separately installed, explicitly enabled, and never
  elevated, installed, or bundled by Roblox One.
- Account/history settings under `%LOCALAPPDATA%\RobloxOne` are private local
  data, not portable release content.

Please report any path that bypasses these boundaries, including unsafe URI
handling, navigation outside official Roblox domains, profile-crossing session
data, untrusted process execution/termination, update verification bypasses,
secret leakage, or unsafe local-API behavior.

## Authentic releases

Use only assets attached to releases in
`https://github.com/Makmatoe/RobloxOne`. A production release is expected to
include a signed release descriptor, Velopack package metadata, an SPDX SBOM,
complete dependency notices, checksums covering every other asset, and a GitHub
artifact attestation. The release verifier rejects unexpected package files and
checks every expected executable's structure. The no-cost releases are not
Authenticode code-signed, so Windows reports an unknown publisher. A GitHub
attestation records build provenance; it does not replace in-app descriptor and
package-hash verification.
