# AGENTS.md instructions for C:\Project\Utility\HotSonos

## Global AGENTS Source
- Read `C:\Project\_instructions\AGENTS.md` first at the start of every task.
- Treat `C:\Project\_instructions\AGENTS.md` as the baseline global policy.
- Apply local rules in this file as repo-specific additions/overrides.

## Project directories
- `C:\Project\Utility\HotSonos`

## Product Spec (Required for Planning/Implementation)
- Use `C:\Project\Utility\HotSonos\spec.md` as the master specification.
- When requirements conflict with older notes or chat context, prefer `spec.md`.
- Any feature/design changes should update `spec.md` in the same task unless the user says otherwise.

## Runtime and Platform Baseline
- Target runtime is `.NET 10+`.
- Target platform is Windows 10/11 (x64).

## Architecture
- `src/HotSonos.Core` — platform-agnostic Sonos local UPnP/SOAP client (discovery, transport, favorites). No Windows/WPF dependency so it stays console-testable.
- `src/HotSonos.Harness` — console harness for proving Core against live speakers.
- `src/HotSonos.App` (later) — WPF system-tray app with global hotkeys; references Core.

## Sonos Control Notes
- Local-only control over UPnP/SOAP on TCP port 1400; discovery via SSDP (UDP 1900). No cloud / no account.
- No third-party Sonos NuGet dependency — the UPnP client is hand-rolled (decided).
