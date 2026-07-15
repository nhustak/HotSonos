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
- **After context compression / new session:** read **`spec.md` §0 Implementation progress** first — live checklist for library plan, uncommitted work, and next step.
- When advancing the library plan (§7.7), update **§0 checklist + status** in the same task (not only code).

## Runtime and Platform Baseline
- Target runtime is `.NET 10+`.
- Target platform is Windows 10/11 (x64).

## Architecture
- `src/HotSonos.Core` — platform-agnostic Sonos local UPnP/SOAP client (discovery, transport, favorites). No Windows/WPF dependency so it stays console-testable.
- `src/HotSonos.Harness` — console harness for proving Core against live speakers.
- `src/HotSonos.App` — WPF system-tray app with global hotkeys + loopback MCP; references Core.

## Sonos Control Notes
- Local-only control over UPnP/SOAP on TCP port 1400; discovery via SSDP (UDP 1900). No cloud / no account.
- No third-party Sonos NuGet dependency — the UPnP client is hand-rolled (decided).

## Loopback MCP (debug / agent tools)
- While the tray app is running with MCP enabled: `http://127.0.0.1:42341/mcp`
- Tools: discovery status (`deviceListPopulated`), groups/zones/offline, refresh_devices, volumes, now_playing, favorites, settings, logs; **library**: `discover_library_roots` (from Sonos A:TRACKS x-file-cifs), `get_library_config`, `get_library_status`, `library_rescan` (auto-discover if roots empty), `library_search`, `library_get_track`; **control**: play_pause, next/previous, volume_up/down, mute, level_volumes, shuffle_library, fresh_start, play_favorite_slot, set_active_room, wake_now, wake_cancel
- Library tag scan needs this **PC** to open the UNC root Sonos reports (SMB credentials); speakers may reach a share Windows currently cannot.
- **UI**: Main window tabs — Settings / Library (search results) / MCP Debug (live tool command log). Tray: Library…, MCP Debug…
- Register in `C:\Project\_mcp\mcp-servers.json` as `hotsonos`, then run `sync-mcp.ps1`
- Product roadmap / live checklist: `spec.md` §0 (next after steps 1–2: tag **write** / `HOTSONOS_TEMPO`)
