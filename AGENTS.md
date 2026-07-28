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
- Tools: discovery status (`deviceListPopulated`), groups/zones/offline, refresh_devices, volumes, now_playing, favorites, settings, logs, **`get_play_events`** (started/skipped/paused/resumed); **`list_library_folders`**, **`play_folder`**; **library**: `discover_library_roots`, `get_library_config`, `get_library_status`, `library_rescan`, `library_search`, `library_get_track`, `list_tags`, `list_genres`, `tag_create`, `tag_rename`, `tag_delete` (purge from all files), `track_toggle_tag`, `track_set_tags` (HOTSONOS_TAGS keys), `track_find_master`, `track_link_master`; **control**: play_pause, next/previous, volume_up/down, mute, level_volumes, shuffle_library, `play_library_track`, `play_tag` (e.g. Favs shuffled → top-up into library shuffle by default), `play_genre` (standard Genre field shuffle → same top-up default), `resume_shuffle`, fresh_start, play_favorite_slot, set_active_room, wake_now, wake_cancel
- Library scan/tag **write** needs this **PC** SMB access (read for scan, write for tags) to the UNC root Sonos reports; master dual-write also needs write access under `MasterLibraryRoot`.
- **Tags**: flat catalog (auto key + label); files store `HOTSONOS_TAGS=key1;key2`; rename label without rewrite. Quick-tag Ctrl+Alt+T; Library chips + keys 1–9; context menu Toggle tag.
- **Genre play**: standard file Genre metadata from SQLite cache; Control / Quick Play / favorite slots / MCP `list_genres` + `play_genre` (not a HotSonos catalog). Toggle `ShowGenresInPlaySources` (Shuffle tab) to hide genres from UI pickers.
- **Control shuffle From**: `ControlShuffleSource` = `all` | `folder:{path}` | `tag:{key}` | `genre:{name}` for Start shuffle button; hotkey shuffle stays Daily mix.
- **UI**: Main window tabs — Control / Hotkeys / Shuffle / Library / Tags / Wake / Options / MCP Debug. Tray: Library…, MCP Debug…
- Register in `C:\Project\_mcp\mcp-servers.json` as `hotsonos`, then run `sync-mcp.ps1`
- Product roadmap / live checklist: `spec.md` §0 (next: playlist create from filter / step 6)
