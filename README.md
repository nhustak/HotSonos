# HotSonos

[![build](https://github.com/nhustak/HotSonos/actions/workflows/build.yml/badge.svg)](https://github.com/nhustak/HotSonos/actions/workflows/build.yml)
[![latest release](https://img.shields.io/github/v/release/nhustak/HotSonos)](https://github.com/nhustak/HotSonos/releases/latest)
[![license](https://img.shields.io/github/license/nhustak/HotSonos)](LICENSE)

**Version 1.0.0.9** · [Releases](https://github.com/nhustak/HotSonos/releases) · [CI](https://github.com/nhustak/HotSonos/actions/workflows/build.yml) · [Spec / roadmap](spec.md)

Windows system-tray utility for controlling a Sonos system with global keyboard shortcuts. Open source ([MIT](LICENSE)), maintained by [Nick Hustak](https://github.com/nhustak).

HotSonos talks to your Sonos speakers entirely over the **local network** (UPnP/SOAP) — no cloud, no Sonos account, no internet round-trips. It lives in the system tray and gives you instant, global hotkeys for the things the Sonos apps make you click through: shuffle your whole library to every speaker, play/pause, skip, and whole-house volume — plus a live now-playing flyout and automatic speaker re-sync.

> Built for Windows 10/11 on .NET 10 (WPF). Works with Sonos S1/S2 players on the same LAN.

### Product direction
- **Today:** history-aware daily shuffle, transport/volume hotkeys, favorites, wake-to-music, live topology, local library cache + tags, loopback MCP for agents.
- **Settings UI:** left vertical nav — Control · Hotkeys · Shuffle · Library · Wake · Options · MCP Debug.
- **Next:** MCP polish; playlist create-from-filter + play (see **[spec.md](spec.md)** §0).
- **MCP:** with the tray app running: `http://127.0.0.1:42341/mcp` (devices, control, library search/tags + master dual-write, logs).

---

## Features

### 🔀 History-aware library shuffle
Groups speakers and builds a short random mix from your music library, leaves out songs you’ve already heard recently, and plays that list straight through. When the list is almost done, it adds another fresh batch the same way. That starts music quickly and keeps the day from repeating what you already listened to.

Trigger with **double-click tray icon**, hotkey, or Control page. Optional **artist spacing** and all parameters (queue size, top-up size, history days, clear history) live under **Settings → Shuffle**.

#### Shuffle FAQ

**What does it do?**  
It builds a short random mix from your library, leaves out songs you’ve heard recently, and plays that list in order. When the list is almost empty, it adds another fresh batch.

**Why a small list instead of the whole library?**  
A short mix starts faster and is more reliable on Sonos. Dumping thousands of songs at once is slow and can be flaky. Small batches keep things snappy and let HotSonos keep the mix fresh as you listen.

**How does it avoid repeats?**  
As songs play, HotSonos remembers them. When it builds the next batch, it skips those recent ones (by default, roughly the last couple of weeks).

**Does it reshuffle what’s already lined up?**  
No. Songs already in the queue stay put. New songs only get added at the end.

**What happens when the queue is almost empty?**  
HotSonos quietly adds another random batch of unheard-recently songs so music keeps going without you doing anything.

**What if I’ve already heard most of my library?**  
If there aren’t enough “fresh” songs left, it loosens the “skip recent” rule so music can still play.

**Is this the same as a saved Sonos playlist?**  
No. This is a live daily mix for whole-house listening. Saved playlists are better for intentional moods (“Jazz,” “Dinner,” etc.).

### 🔄 Restart fresh (re-sync + reshuffle)
Re-discovers speakers, force-regroups them, and starts a new history-aware shuffle. Tray item, Control button, optional hotkey.

### ⏯️ Transport & volume hotkeys
Play/pause, next, previous, volume up/down, mute, and level-all — from any app. Re-bindable under **Hotkeys**.

### 🔉 Level all speakers
One click (or hotkey) sets every speaker to the same absolute volume (default 20%) and unmutes them.

### 🎴 Live Now-Playing flyout
Album art, title, artist, state — GENA push updates. Draggable, pinnable; toggles under **Options**.

### 📡 Live speaker monitoring
Topology events: offline tray indicator, reconnect toasts, auto-rejoin active group, live room picker.

### 🎚️ Per-speaker volume
Control page shows every speaker with volume slider and mute.

### ☀️ Wake to music
Scheduled start on a room, volume ramp, favorite or shuffle source, optional whole-house expand + shuffle. Skips if already playing.

### 📚 Local library cache & tags
- **Discover from Sonos** (share roots from `x-file-cifs` URIs)  
- SQLite cache of FLAC/MP3 metadata (format, bit depth, sample rate, bitrate)  
- **Sonos-unplayable** heuristic for hi-res / out-of-spec files  
- Write **`HOTSONOS_TEMPO`** (and optional standard tags) into files; MCP `track_set_tags`  
- Paths / rescan / search under **Library**

### 🤖 Loopback MCP
While the app runs with MCP enabled: `http://127.0.0.1:42341/mcp` — discovery, control, library tools, logs. Live command log on **MCP Debug**. Register via `C:\Project\_mcp` if you use the multi-agent MCP hub.

### Other
- Single-instance tray app; second launch activates the running window  
- Optional **Start with Windows**; nightly silent re-sync  
- Config: `%LocalAppData%\HotSonos\settings.json`  
- Play history: `%LocalAppData%\HotSonos\play-history.json`  
- Logs: `%LocalAppData%\HotSonos\logs`

---

## Default hotkeys

| Action | Default shortcut |
|---|---|
| Shuffle Music Library → all speakers | `Ctrl + Alt + F8` (also **double-click tray icon**) |
| Play / Pause | `Ctrl + Alt + F9` |
| Previous track | `Ctrl + Alt + F10` |
| Next track | `Ctrl + Alt + F11` |
| Volume up | `Ctrl + Alt + ↑` |
| Volume down | `Ctrl + Alt + ↓` |
| Mute / Unmute | `Ctrl + Alt + M` |
| Level all / Restart fresh / favorite slots | unassigned (set your own) |

Re-bind under **Hotkeys** (tray → *Open HotSonos*).

---

## Install

Download the latest **`HotSonos-x.y.z.msi`** from the [Releases page](https://github.com/nhustak/HotSonos/releases/latest) and run it. Per-user install (no admin) to `%LocalAppData%\Programs\HotSonos`, Start Menu shortcut, .NET runtime bundled. Uninstall from **Settings → Apps**.

> The MSI is **unsigned**, so SmartScreen may prompt **More info → Run anyway**.

Each [GitHub Release](https://github.com/nhustak/HotSonos/releases) is produced by CI when a version tag (`v*`) is pushed. Pushes to `master` run [build + test + MSI](https://github.com/nhustak/HotSonos/actions/workflows/build.yml).

## Requirements

- Windows 10 or 11  
- Sonos players (S1 or S2) on the same LAN  
- Install from Releases: nothing else  
- Build from source: [.NET 10 SDK](https://dotnet.microsoft.com/download)  
- Library scan/tag write: this PC needs SMB access to the music share Sonos indexes  

---

## Build & run

```powershell
git clone https://github.com/nhustak/HotSonos.git
cd HotSonos
dotnet build HotSonos.slnx
dotnet test HotSonos.slnx
dotnet run --project src/HotSonos.App
```

First launch may prompt for **Private network** access (GENA callbacks).

### Console harness

```powershell
dotnet run --project src/HotSonos.Harness -- zones
dotnet run --project src/HotSonos.Harness -- --room "Living Room" favorites
dotnet run --project src/HotSonos.Harness -- --room "Living Room" shuffle
```

### Project layout
| Project | Purpose |
|---|---|
| `HotSonos.Core` | UPnP client (discovery, control, events, shuffle) |
| `HotSonos.App` | Tray app: hotkeys, settings, library, MCP, wake |
| `HotSonos.Harness` | Console tester |
| `HotSonos.Core.Tests` | Offline unit tests |

Version is single-sourced in `Directory.Build.props`; release tags override with `-p:Version=…`.

---

## How it works

- **Discovery** — SSDP across interfaces; topology from any responding player.  
- **Control** — SOAP on TCP **1400** to group coordinators.  
- **Shuffle** — browse `A:TRACKS`, exclude recently **played** tracks, short queue (~80 default), auto **top-up** near end, play in `NORMAL` mode.  
- **Library** — optional filesystem scan/tag index under discovered UNC roots; tags live in files; SQLite is a rebuildable cache.  
- **MCP** — Kestrel loopback host inside the tray process.  
- **GENA** — local listener for now-playing and topology.

---

## Notes & limitations

- Speakers out of sync is usually Wi‑Fi; Restart fresh / nightly re-sync help.  
- Nightly re-sync and wake need the **PC awake** with HotSonos running.  
- Sonos does not reliably report “can’t play this file”; unplayable flags are **format heuristics**.  
- Shuffle history only reshapes the queue at **rebuild/top-up**, not mid-queue.  
- GENA callback is for a **trusted home LAN**.

---

## Changelog

### 1.0.0.9
- **History-aware shuffle**: short queues, hard-exclude played tracks, auto top-up near end, artist spacing; Settings under **Shuffle** (clear history, all parameters)
- **Library intelligence**: discover roots from Sonos, SQLite FLAC/MP3 cache, format / Sonos-unplayable flags, write `HOTSONOS_TEMPO` + standard tags (`track_set_tags`)
- **UI**: left vertical navigation (Control, Hotkeys, Shuffle, Library, Wake, Options, MCP Debug)
- **MCP**: library tools + control tools; live MCP command log
- Play history file: `%LocalAppData%\HotSonos\play-history.json`

### 1.0.0.8
- **Loopback MCP** inside the tray app; Settings toggle + port; tray copies endpoint

### 1.0.0.7
- Living **[spec.md](spec.md)** roadmap

### 1.0.0.6
- **Wake to music**; Settings auto-refresh devices on open

### 1.0.0.5
- Diagnostics, playlist-by-id, exclusive shuffle gate, Core unit tests

### 1.0.0.4
- Fresh Start flyout feedback; live per-speaker volumes

---

## License

[MIT](LICENSE) © 2026 Nick Hustak. Provided as-is with no warranty. Not affiliated with Sonos, Inc.
