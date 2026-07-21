## Summary

Describe the user-visible change and link the approved issue.

## Safety and privacy

- [ ] I did not add credentials, cookies, launch tickets, private-server codes,
      local account data, signing material, or generated release artifacts.
- [ ] New network traffic, persistence, process control, browser behavior,
      dependencies, and update/signing changes are explained below.
- [ ] Roblox network traffic remains limited to official Roblox endpoints.
- [ ] Optional local integrations remain loopback-only and opt-in.

Safety/privacy notes:

## Validation

- [ ] `dotnet restore`
- [ ] `dotnet build -c Release --no-restore`
- [ ] `dotnet test -c Release --no-build`
- [ ] Relevant manual behavior was exercised on Windows x64.
- [ ] Tests and documentation were added or updated where needed.

Validation details:

## Screenshots

Include sanitized screenshots only when useful. Remove account names, IDs,
private-server data, and local paths containing personal information.
