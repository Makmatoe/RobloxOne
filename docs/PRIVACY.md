# Privacy and local data

Roblox One is local-first. It has no project-operated account service, cloud
database, advertising system, or telemetry collector.

## Data stored on the computer

Roblox One stores application settings and isolated browser profiles under
`%LOCALAPPDATA%\RobloxOne`. Depending on features used, this can include:

- local account-slot identifiers, Roblox user ID/username after Roblox reports
  them, custom labels, and accent colors;
- a separate WebView2 profile per account, including Roblox cookies and browser
  storage controlled by Roblox;
- each account's selected destination;
- shared Recent/Favorite metadata, timestamps, experience names, public/private
  classification, and a server JobId when a best-effort local match succeeds;
- private-server codes only when the user explicitly saves or launches such a
  destination;
- sound preferences and the safe local filename of an imported sound; and
- optional local integration configuration or connection metadata created by
  those separately installed integrations.

Roblox One does not intentionally store Roblox passwords, launch tickets, raw
Roblox Player logs, server IP addresses, HandleScope bearer tokens, or raw
handle values. Never send the `%LOCALAPPDATA%\RobloxOne` directory in a public
bug report because its WebView2 profiles may contain authenticated cookies.

## Network connections

Roblox One connects to official Roblox HTTPS endpoints when the user signs in,
verifies an account, resolves supported destinations, looks up experience
metadata, or requests a launch ticket. Roblox receives data according to its
own privacy policy and account settings.

When the user explicitly checks for an application update, Roblox One connects
to GitHub Releases for `Makmatoe/RobloxOne`. GitHub receives ordinary request
metadata such as the source IP address and user agent under GitHub's policies.

An optional post-launch HTTP hook is used only after the user configures a
loopback URL. The connector rejects non-loopback destinations, redirects, and
system proxies. Its bounded event payload excludes passwords, cookies, launch
tickets, destinations, and private-server codes.

The optional HandleScope integration connects only to an already-running,
separately installed local API after the user enables and configures it.
Roblox One does not download, bundle, install, elevate, or remotely contact
HandleScope.

## Browser permissions

Account pages run in Microsoft WebView2 profiles. Roblox One limits top-level
navigation to official Roblox HTTPS domains and blocks downloads, external app
protocols, password autofill integration, and camera, microphone, location, and
notification permissions. Microsoft services may install or update the
WebView2 Runtime independently of Roblox One.

## Deleting local data

Removing an account in Roblox One is intended to delete that account slot's
complete local WebView2 profile, including cookies, local storage, cache,
history, service workers, and autofill data. Clear Recent/Public/Private history
with the corresponding in-app controls; clearing history does not remove pinned
Favorites unless the user removes those entries separately.

To remove all Roblox One data, first remove accounts in the app, close Roblox
One, and then delete `%LOCALAPPDATA%\RobloxOne`. This action signs those local
profiles out by removing their data; it does not revoke Roblox sessions on
other devices. Use Roblox account security controls when global session
revocation is needed.

Application updates replace application files and normally preserve this local
data. Published release artifacts never include a developer's or another user's
local data.
