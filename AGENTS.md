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
- `src/HotSonos.Remote` — .NET MAUI remote (`net10.0-android`, minSdk 24; `net10.0-ios` for iPhone and iPad when `HotSonosRemoteTargetFrameworks` is set or the build is on macOS). Full control of the tray app over the LAN. Not in `HotSonos.slnx`. iOS setup: `docs/ios.md`.
- `src/HotSonos.Remote.Protocol` — shared phone DTOs, PIN compare, and Daily speaker-group edits.

## Sonos Control Notes
- Local-only control over UPnP/SOAP on TCP port 1400; discovery via SSDP (UDP 1900). No cloud / no account.
- No third-party Sonos NuGet dependency — the UPnP client is hand-rolled (decided).

## Loopback MCP (debug / agent tools)
- While the tray app is running with MCP enabled: `http://127.0.0.1:42341/mcp`
- Phone remote (separate from MCP): LAN `http://<pc>:42342`, header `X-HotSonos-Pin` on every route. PC mode is a full view of this tray app. Standalone mode discovers speakers and uses `HotSonos.Core` (SSDP + SOAP) with no PC. Link: Auto / PC only / Speakers only. Do not bind MCP to the LAN. Top-bar **Phone** shows the address and PIN. See spec §6.6. The 6-digit PIN stays; do not switch it to a HotList-style token.
- Tools: discovery status (`deviceListPopulated`), groups/zones/offline, refresh_devices, volumes, now_playing, favorites, settings, logs, **`get_play_events`**, **`get_heard_tracks`** (non-library tracks heard, Control tab), **`get_topology_events`** / **`list_topology_members`** (rooms/Sub/Port join-leave; Topology tab + `topology-events.jsonl`); **`list_library_folders`**, **`play_folder`**; **library**: `discover_library_roots`, `get_library_config`, `get_library_status`, `library_rescan`, `library_search`, `library_get_track`, `list_tags`, `list_genres`, `tag_create`, `tag_rename`, `tag_delete` (purge from all files), `track_toggle_tag`, `track_set_tags` (HOTSONOS_TAGS keys), `track_find_master`, `track_link_master`; **control**: play_pause, next/previous, volume_up/down, mute, level_volumes, shuffle_library, **`play_library_track`** (default play: keep the queue, song on top), **`queue_track`** / **`queue_now`** ("queue X" = up next, no skip), `replace_queue_with_track` (wipe — not the default), `play_tag` (e.g. Favs shuffled → top-up into library shuffle by default), `play_genre` (standard Genre field shuffle → same top-up default), `resume_shuffle`, fresh_start, play_favorite_slot, set_active_room, wake_now, wake_cancel, **`list_spotify_playlists`** (saved named links), **`play_spotify`** (a link or a saved name; switches the group onto that Spotify item)
- Library scan/tag **write** needs this **PC** SMB access (read for scan, write for tags) to the UNC root Sonos reports; master dual-write also needs write access under `MasterLibraryRoot`.
- **Tags**: flat catalog (auto key + label); files store `HOTSONOS_TAGS=key1;key2`; rename label without rewrite. Quick-tag Ctrl+Alt+T; Library chips + keys 1–9; context menu Toggle tag.
- **Genre play**: standard file Genre metadata from SQLite cache; Play tab / Quick Play / favorite slots / MCP `list_genres` + `play_genre` (not a HotSonos catalog). Toggle `ShowGenresInPlaySources` (Shuffle tab) to hide genres from UI pickers.
- **Control shuffle From**: `ControlShuffleSource` = `all` | `folder:{path}` | `tag:{key}` | `genre:{name}` for Start shuffle button; hotkey shuffle stays Daily mix.
- **UI**: Main window tabs — Control / Play (folders, tags, genres) / Hotkeys / Shuffle / Library / Tags / Wake / Options / Topology / Reports / Logs / Debug. Sonos favorites and saved Spotify links stay on Control. Top bar: **Phone**. Tray: Phone…, Library…, Topology…, Logs…, Reports…, Debug…  
- **Phone**: First launch with no pairing opens Connect (UDP find on 42343, or type the LAN address and PIN). Then MAUI tabs Player / Now / Play / Queue / Library / Rooms / Reports / Settings. Player is the landing screen (art, title, artist, album, icon transport, volume, Mute, Level all) and works on the PC or the speakers. Now is favorite slots and Sonos favorites. Settings drills into Connection / PC, Playback & Shuffle, Spotify links, Library, Speakers & Daily group, Wake & nightly, and About. The top line (not on Player) says `PC`, `Speakers`, `Speakers: room`, `PC unreachable`, or `PC: not paired` and opens Connection / PC. The address stays on Connect and that page. PC only is what Connect saves. Polls now-playing every 2s. Destructive actions confirm on the phone. Build Android: `dotnet build src/HotSonos.Remote/HotSonos.Remote.csproj -f net10.0-android` (Windows and Linux CI stay on that). iOS is Codemagic (`codemagic.yaml`, `docs/ios.md`) via `-p:HotSonosRemoteTargetFrameworks=net10.0-ios`; do not pass a global `TargetFrameworks` (that would retarget Protocol). Release publish signs with `%USERPROFILE%\.android-keys\hotsonos\` when present. The tray Phone window also has **Phone wireless debugging** (adb IP and connect port saved as `PhoneAdbHost` / `PhoneAdbPort` and `%LocalAppData%\HotSonos\phone-adb.txt`). The pairing code is not saved or logged. Chromebooks run this same Android package (resizable window, keyboard and mouse, typed PC address when UDP 42343 cannot cross the container); see README ChromeOS.
- **Reports**: `%LocalAppData%\HotSonos\activity.db` (SQLite, on while the app runs). Song Play History, Music, and Event Log, 500 rows per page with Load older. Shuffle last-played stays in `play-history.json` and is not rewritten by the activity log. Add a report via `ReportCatalog` plus a UserControl.
- Register in `C:\Project\_mcp\mcp-servers.json` as `hotsonos`, then run `sync-mcp.ps1`
- Product roadmap / live checklist: `spec.md` §0 (next: playlist create from filter / step 6)
