# HotSonos — Specification

> Status: **DRAFT / design** (2026-06-14). No code yet. Open questions at the bottom.

## Overview
- **Platform**: Windows desktop only
- **Framework**: .NET 10 (WPF + WinForms tray), mirrors the HotNotify utility
- **Purpose**: Control a Sonos system from global keyboard shortcuts — something the Sonos desktop/mobile apps do not offer
- **Primary use case**: Hit a hotkey from anywhere (any app focused) to play/pause, skip, and launch a specific playlist/favorite, without alt-tabbing to the Sonos app
- **Philosophy**: Tray-resident, local-only (no cloud, no account), instant response, practical over feature-complete

## Feasibility (confirmed)
Sonos speakers expose a local UPnP/SOAP control server on **TCP port 1400**, discoverable via SSDP multicast (UDP 1900). This is the long-standing, undocumented-but-stable interface used by SoCo, node-sonos, and the official apps. No internet, OAuth, or developer registration required. Everything in scope maps to it:

| Feature | Local mechanism |
|---|---|
| Play / Pause | `AVTransport` SOAP action `Play` / `Pause` |
| Next / Previous | `AVTransport` `Next` / `Previous` |
| Play a Sonos Favorite | `ContentDirectory.Browse` of `FV:2` → `SetAVTransportURI` (with the favorite's resMD DIDL) → `Play` |
| Play a Sonos Playlist (saved queue) | Separate container `SQ:`. The `file://` res is NOT playable on current firmware (returns 804/714). Correct method: enqueue the container via `x-rincon-playlist:{uuid}#SQ:N` (server expands), then play the queue — same pattern as the library shuffle. |
| Shuffle whole Music Library | `x-rincon-playlist:{uuid}#A:TRACKS` enqueue + `SetPlayMode SHUFFLE` + play queue (see below) |
| Volume up/down/mute (stretch) | `RenderingControl` `SetRelativeVolume` / `SetMute` |
| Current track / state display | `AVTransport.GetTransportInfo` + `GetPositionInfo` |

The official Sonos **cloud Control API** was evaluated and rejected: it requires OAuth + internet round-trips, adding latency and a failure mode that is unacceptable for a hotkey utility.

## Architecture

### Sonos control layer (decision: roll our own thin UPnP client)
A small, self-contained UPnP/SOAP client (~300 LOC) rather than a NuGet dependency.
- **Rejected** `ByteDev.Sonos` (last updated 2021, .NET Standard 2.0, weak favorites support).
- **Rejected** `Sonos.Base` / `sonos-net` (targets .NET 10, actively maintained by svrooij, but author labels it "far from complete, just an experiment").
- Rolling our own keeps zero dependency risk and gives full control over the favorites/playlist flow, which is the headline feature. The SOAP envelopes are stable and well documented (SoCo wiki).

Components:
- **`SonosDiscovery`** — SSDP `M-SEARCH` to discover speakers + coordinators; parses `http://{ip}:1400/xml/device_description.xml` for room names, zone/group topology.
- **`SonosDevice`** — wraps SOAP calls to one speaker (AVTransport, ContentDirectory, RenderingControl).
- **`SonosController`** — high-level intents (PlayPause, Next, Previous, PlayFavorite(name)); resolves which group/coordinator to target.
- **`SonosFavoritesService`** — browses `FV:2`, caches the list of favorites/playlists for the picker and for hotkey binding by name.

### App shell (mirrors HotNotify layout)
- `App.xaml` / `App.xaml.cs` — single-instance, tray bootstrap
- `Infrastructure/` — `TrayController`, `AppVersion`, `WindowsStartupManager`, **`GlobalHotkeyManager`**
- `Services/` — `SonosController`, `SonosDiscovery`, `SonosFavoritesService`, `ConfigStore`
- `Models/` — `SonosZone`, `SonosFavorite`, `HotkeyBinding`, `AppSettings`
- `Windows/` — main settings window (hotkey editor + zone picker), optional now-playing toast

### Global hotkeys
Win32 `RegisterHotKey` / `WM_HOTKEY` via a message-only `HwndSource` (standard WPF approach, same process model as HotNotify's tray hooks). Each binding = modifier mask + virtual key + an action (PlayPause / Next / Previous / PlayFavorite[favoriteName]). Conflicts surfaced in the editor when registration fails.

## Core features

### System tray application
- Runs in the system tray with a custom HotSonos icon
- Shows app version in tooltip and at top of context menu
- Right-click menu: Open HotSonos · Play/Pause · Next · Previous · (target room submenu) · Exit
- Optionally launches at Windows startup

### Hotkeys
- Default bindings (user-editable):
  - **🔀 Shuffle Music Library (Ctrl+Alt+F8) — the primary action.** Shuffle-plays the entire local Music Library on the active group. This is what the user wants ~99.9% of the time.
  - Play/Pause (Ctrl+Alt+F9), Previous (F10), Next (F11) — global media-style shortcuts
  - **4 "Play favorite" slots** (decided), each bound to a chosen Sonos Favorite/Playlist, all targeting the single active room/group

### Music Library shuffle (primary feature)
- **The shuffle key first groups ALL speakers** under the active coordinator (each visible player gets `SetAVTransportURI` `x-rincon:{coordinatorUuid}`), then shuffles — guaranteeing whole-house playback even if a room was ungrouped. The user wants "all speakers" ~99.9% of the time.
- The Music Library (`A:` containers) "all tracks" container is `A:TRACKS`, enqueued in a **single** `AddURIToQueue` call via `x-rincon-playlist:{coordinatorUuid}#A:TRACKS` (server expands it — no track-by-track). Then `SetPlayMode SHUFFLE`, point transport at `x-rincon-queue:{uuid}#0`, and Play.
- Verified live: enqueued 1,719 tracks in one call and shuffle-played; group-join command verified.
- Play/Pause and Next then control the whole grouped house (commands route to the coordinator).
- Editor lets the user capture a key chord, pick an action, and (for favorites) pick from the discovered favorites list
- Registration conflicts reported inline

### Target zone selection
- Sonos is multi-room; commands must target a **group coordinator**
- User picks a default "active room/group"; tray submenu allows quick switching
- Transport commands route to that group's coordinator

### Now-playing feedback (v1 — decided in)
- Brief topmost toast on action ("▶ Playing: <track>" / "Playlist: <name>"), auto-dismiss
- Reuses HotNotify's popup styling

### Configuration / data storage
- **JSON file** at `%LocalAppData%\HotSonos\settings.json` (decided — simpler than HotNotify's SQLite for this small config), holding:
  - Default zone (room name + UUID; re-resolve IP on launch since DHCP)
  - Hotkey bindings (modifier, key, action, favorite name)
  - **4 "play favorite" slots** (decided)
  - Launch-at-startup flag, toast on/off
- Favorites are read live from the speaker, not stored

## Out of scope (v1)
- Grouping/ungrouping rooms, stereo pairing
- Volume EQ, sleep timers, alarms
- Streaming-service auth or search (we only launch existing favorites/playlists)
- Cloud Control API
- Non-Windows platforms

## Decisions (2026-06-14)
- **Config store**: JSON file at `%LocalAppData%\HotSonos\settings.json`.
- **Favorite hotkey slots**: 4, all targeting a single configured active room/group (tray menu can switch the active room).
- **Now-playing toast**: in for v1 (reuse HotNotify popup styling).

## Remaining open question
- **Speaker generation**: confirm units are Sonos S2 (current app). Local UPnP works across S1/S2; just worth confirming no newer-firmware unit behaves differently before Phase 1 testing.
