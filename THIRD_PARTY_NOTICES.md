# Third-party notices

Roblox One depends on third-party software. Each component remains governed by
its own license; the Roblox One license does not replace those terms.

## Microsoft WebView2

- Package: `Microsoft.Web.WebView2`
- Publisher: Microsoft Corporation
- Project/package information:
  <https://www.nuget.org/packages/Microsoft.Web.WebView2>
- License: see the license identified by the package and the Microsoft Edge
  WebView2 Runtime terms applicable to the installed runtime.

WebView2 provides the isolated embedded browser used for official Roblox sign-in
pages. The WebView2 Runtime is installed and serviced separately by Microsoft.

## Velopack

- Package and tooling: `Velopack` / `vpk`
- Project: <https://github.com/velopack/velopack>
- License: MIT
- Copyright: the Velopack contributors

Velopack provides release packaging and the user-confirmed update mechanism.

## Development and test tooling

The repository uses `Microsoft.NET.Test.Sdk`, `xunit.v3`, and
`xunit.runner.visualstudio` only to execute automated tests. These packages are
not included in the Roblox One application or release package. They remain
subject to the licenses identified in their NuGet packages and upstream
projects.

## .NET and bundled runtime components

Self-contained releases include Microsoft .NET runtime components and may
include other native/runtime files selected by the .NET SDK. Their copyright
and license notices are included in, or referenced by, the published release as
required by their respective licenses. See <https://dotnet.microsoft.com/> and
the notices emitted by the exact SDK/runtime used for that release.

## External optional software

Roblox Player, the Microsoft Edge WebView2 Runtime, and HandleScope are not
licensed as part of Roblox One. HandleScope is optional, separately installed,
and not bundled in Roblox One releases. Users must obtain and operate external
software under its publisher's terms.

This notice is informational and is not a substitute for the complete license
files supplied by third-party publishers.
