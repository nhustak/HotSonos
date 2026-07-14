# HotSonos — Specification

> Status: **Implemented (v1.0+)** — kept in sync with the shipped app. Prefer this file and `README.md` over chat history when requirements conflict.

## Overview
- **Platform**: Windows desktop only (Windows 10/11 x64)
- **Framework**: .NET 10 (WPF + WinForms tray)
- **Purpose**: Control a Sonos system from global keyboard shortcuts — something the Sonos desktop/mobile apps do not offer
- **Primary use case**: Hit a hotkey from anywhere to play/pause, skip, volume, and shuffle the whole local Music Library to every speaker, without alt-tabbing to the Sonos app
- **Philosophy**: Tray-resident, local-only (no cloud, no account), instant response, practical over feature-complete

## Feasibility (confirmed)
Sonos speakers expose a local UPnP/SOAP control server on **TCP port 1400**, discoverable via SSDP multicast (UDP 1900). No internet, OAuth, or developer registration required.

| Feature | Local mechanism |
|---|---|
| Play / Pause | `AVTransport` SOAP action `Play` / `Pause` |
| Next / Previous | `AVTransport` `Next` / `Previous` |
| Play a Sonos Favorite | `ContentDirectory.Browse` of `FV:2` → `SetAVTransportURI` + `Play` |
| Play a Sonos Playlist (saved queue) | Browse `SQ:` → enqueue via `x-rincon-playlist:{uuid}#SQ:N` → play queue |
| Shuffle whole Music Library | Browse `A:TRACKS` client-side, Fisher-Yates shuffle, batch `AddMultipleURIsToQueue`, play queue in `NORMAL` mode |
| Volume up/down/mute | Per-member `RenderingControl` (group write actions 803 on systems with fixed-volume members) |
| Level all speakers | Absolute `SetVolume` + unmute on every visible player |
| Current track / state | GENA AVTransport events (`LastChange`); fallback SOAP if needed |
| Topology / drop detection | GENA ZoneGroupTopology events |

Official Sonos **cloud Control API** was evaluated and rejected (OAuth + internet latency).

## Architecture

### Projects
| Project | Role |
|---|---|
| `src/HotSonos.Core` | Platform-agnostic UPnP/SOAP client (discovery, transport, favorites, GENA). No WPF. |
| `src/HotSonos.App` | WPF tray app: hotkeys, settings, flyout, nightly reset |
| `src/HotSonos.Harness` | Console harness against live speakers |
| `tests/HotSonos.Core.Tests` | Offline unit tests for parsers / playability |

### Core components
- **`SonosDiscovery`** — SSDP `M-SEARCH` **per usable IPv4 interface** (multi-homed safe), then full topology via `GetZoneGroupState`
- **`SonosSoapClient`** — thin SOAP envelope POST to `http://{ip}:1400{controlPath}`
- **`SonosController`** — high-level intents against one group coordinator
- **`SonosEventSubscriber`** — GENA SUBSCRIBE + local TCP callback listener; renew loop via `PeriodicTimer`

### App shell
- `App.xaml.cs` — single-instance mutex, tray bootstrap, exclusive gate for long actions
- `Infrastructure/` — `TrayController`, `GlobalHotkeyManager`, `WindowsStartupManager`, `AppVersion`, `AppLog`
- `Services/` — `SonosManager`, `ConfigStore`, `WakeMusicService`
- `Windows/` — Settings (`MainWindow`), `NowPlayingFlyout`

### Global hotkeys
Win32 `RegisterHotKey` / `WM_HOTKEY` via a message-only `HwndSource`. Conflicts surface when registration fails.

## Core features (shipped)

### System tray
- Custom icon; version in tooltip and menu
- Right-click: Open HotSonos, refresh, transport, volume, room submenu, favorites, offline indicator, Exit
- Double-click tray = shuffle library to all speakers
- Optional Start with Windows (`HKCU\...\Run` with `--autorun`; autorun stays silent in tray)

### Hotkeys (defaults)
| Action | Default |
|---|---|
| Shuffle Music Library → all speakers | Ctrl+Alt+F8 |
| Play / Pause | Ctrl+Alt+F9 |
| Previous / Next | Ctrl+Alt+F10 / F11 |
| Volume up / down / mute | Ctrl+Alt+↑ / ↓ / M |
| Level all / Fresh start / 4 favorite slots | Unassigned |

### Music Library shuffle (primary)
1. Group **all** visible players under the active coordinator (`SetAVTransportURI` `x-rincon:{uuid}`)
2. Browse full `A:TRACKS` (paginated), client-side Fisher-Yates shuffle
3. Clear queue; enqueue pre-shuffled tracks in batches of 16 via `AddMultipleURIsToQueue`
4. Point transport at `x-rincon-queue:{uuid}#0`, `SetPlayMode NORMAL`, Play

Device `SHUFFLE` mode is intentionally **not** used — it reuses a deterministic order for a given queue content.

**Fresh start**: re-discover + regroup + shuffle. Immediate flyout “re-syncing…” feedback. Exclusive with concurrent shuffle (second press shows Busy).

### Favorites / playlists
- Four hotkey slots, all targeting the active room/group
- Favorites (`FV:2`): require a non-empty playable `<res>` URI
- Playlists (`SQ:`): playable by **container id** even when `<res>` is empty or `file://`
- Browse is paginated (200/page)

### Target zone
- Commands route to the group **coordinator**
- Groups labeled like the Sonos app (“All Speakers”, “Kitchen + 2”, …)
- Tray submenu switches active group; room name persisted in settings

### Now-playing flyout
- Custom topmost card (not OS toast): art + title + artist + state/action line
- Draggable (position saved), pinnable, independent toggles for track-change vs action
- Connectivity messages (drop / rejoin) also use the flyout

### Live speaker monitoring
- Topology GENA: offline indicator, drop/rejoin messages
- Reconnected speakers auto-rejoined to the active group
- First topology snapshot does not fire “just dropped” alerts for already-offline rooms

### Volume
- Whole-group relative step + mute (per-member writes)
- Level-all to configurable absolute % (skips/fails fixed-volume devices; toast counts successes)
- Per-speaker sliders in Settings (refreshed when Settings reopens)

### Nightly silent re-sync
- Optional (default 3:00 AM local): re-discover + regroup if **nothing** is playing
- Optional reshuffle (starts playback) — off by default
- Only while PC is awake and HotSonos is running

### Wake to music
- Optional alarm: selected **days of week** + clock time
- Starts on a **chosen room/group** only (does not change hotkey active room permanently)
- Start volume, end volume, step %, interval minutes — absolute volume steps on that group
- Play source: **shuffle library** or a **favorite/playlist**
- Optional after ramp: **expand to all speakers** + **full library shuffle** at end volume
- Tray **Stop wake / volume ramp**; volume hotkeys cancel the ramp (skip expand)
- Only while PC is awake and HotSonos is running

### Configuration
- JSON at `%LocalAppData%\HotSonos\settings.json`
- Favorites list is **not** stored (read live from speakers)
- Version single-sourced from `Directory.Build.props`

### Diagnostics
- Rolling daily logs at `%LocalAppData%\HotSonos\logs\hotsonos-yyyyMMdd.log` (7-day retention)
- In-memory ring (last 500 lines) for **Copy diagnostics** tray action
- Tray: **Open log folder** / **Copy diagnostics**
- App still swallows non-fatal errors (must not vanish) but records them via `AppLog`

### Packaging
- Per-user WiX MSI (no admin) → `%LocalAppData%\Programs\HotSonos`
- Self-contained win-x64 publish; .NET runtime bundled
- CI: build + MSI artifact; release workflow tags MSI as `HotSonos-x.y.z.msi`

## Out of scope (v1)
- Manual multi-room grouping UI / stereo pairing editor
- EQ, multi-alarm, snooze, fade-out sleep timer, wake PC from sleep
- Streaming-service auth or search
- Cloud Control API
- Non-Windows platforms
- Code-signed MSI (SmartScreen may warn)

## Engineering notes
- **Action concurrency**: shuffle / fresh start use a non-blocking exclusive gate; other actions wait for the gate so they do not interleave with queue rebuilds
- **GENA callback** listens on a local ephemeral port (`IPAddress.Any`); intended for trusted home LAN only
- **Diagnostics**: file + ring buffer (`AppLog`); no third-party logging package
- **Tests**: offline Core parser tests under `tests/`; live proof via Harness

## Decisions
- Config: JSON file (not SQLite)
- Four favorite slots, one active room/group
- Client-side library shuffle (not device SHUFFLE)
- Now-playing custom flyout (not Windows toast balloons for track feedback)
- Hand-rolled UPnP client (no third-party Sonos NuGet)
- No cloud dependency
