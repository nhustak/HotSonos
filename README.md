# HotSonos

[![build](https://github.com/nhustak/HotSonos/actions/workflows/build.yml/badge.svg)](https://github.com/nhustak/HotSonos/actions/workflows/build.yml)

**Version 1.0.0** · Windows system-tray utility for controlling a Sonos system with global keyboard shortcuts.

HotSonos talks to your Sonos speakers entirely over the **local network** (UPnP/SOAP) — no cloud, no Sonos account, no internet round-trips. It lives in the system tray and gives you instant, global hotkeys for the things the Sonos apps make you click through: shuffle your whole library to every speaker, play/pause, skip, and whole-house volume — plus a live now-playing flyout and automatic speaker re-sync.

> Built for Windows 10/11 on .NET 10 (WPF). Works with Sonos S1/S2 players on the same LAN.

---

## Features

### 🔀 Shuffle your entire Music Library to all speakers
The headline feature. One action groups **every** speaker under a single coordinator and shuffle-plays your whole local Music Library — the entire library is enqueued in a single call (the speaker expands it server-side, so it's instant even for thousands of tracks). Trigger it by **double-clicking the tray icon** or with a hotkey.

### 🔄 Restart fresh (re-sync + reshuffle)
Re-discovers your speakers, force-regroups them all (which clears an out-of-sync state), and starts a brand-new shuffle. Use it when speakers drift apart. Available as a button in Settings, a tray item, and an optional hotkey.

### ⏯️ Transport hotkeys
Global play/pause, next track, and previous track — from any app, anywhere.

### 🔊 Whole-house volume hotkeys
Volume up, volume down, and mute/unmute across the entire group, with a configurable step size.

### 🔉 Level all speakers
One click (or hotkey) sets **every** speaker in the house to the same absolute volume — default 20%, configurable — and unmutes them. Great for resetting after someone cranked one room. Unlike the proportional volume hotkeys, this slams every player to the exact level. Fixed-volume devices (Sub, Port/Amp on line-out) are skipped.

### 🎴 Live Now-Playing flyout
A custom, lightweight flyout (not the Windows toast) showing **album art + title + artist + state**, updated in real time via Sonos push events — so it reflects track changes even when you change songs from the phone app. It's **draggable** (position is remembered), **pinnable** (keep it on screen), and has independent toggles for "show on every track change" and "show when I trigger an action."

### 📡 Live speaker monitoring
HotSonos subscribes to Sonos topology events, so it knows the instant a speaker drops off or rejoins:
- A tray indicator shows **"All speakers online"** or **"⚠ Offline: <room>"**
- Balloon alerts on drop (**"⚠️ Kitchen dropped off"**) and reconnect (**"✓ Kitchen reconnected"**)
- The room/group picker auto-refreshes on any grouping change

### 🌙 Nightly silent re-sync
Optionally, once a night (default 3:00 AM), HotSonos silently regroups every speaker so you wake up to a synced system. **It never starts playback** — if anything is playing at that time, it skips entirely.

### 🎚️ Group-aware room picker
Targets are shown as Sonos groups (e.g. a group containing every player shows as **"All Speakers"**). Commands route to the group coordinator automatically.

### Other
- System-tray app, single-instance, optional **Start with Windows**
- Plain-JSON config at `%LocalAppData%\HotSonos\settings.json`
- Four assignable "play a favorite/playlist" hotkey slots

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
| Level all speakers / Restart fresh / favorite slots | unassigned (set your own) |

All shortcuts are re-bindable in **Settings** (right-click the tray icon → *Open HotSonos*).

---

## Install

Download the latest **`HotSonos-x.y.z.msi`** from the [Releases page](https://github.com/nhustak/HotSonos/releases) and run it. It's a **per-user** install (no admin/UAC) to `%LocalAppData%\Programs\HotSonos`, with a Start Menu shortcut, and the .NET runtime is bundled — nothing else to install. Uninstall any time from **Settings → Apps**.

> The MSI is **unsigned**, so Windows SmartScreen may show an "unknown publisher" prompt on first run — choose **More info → Run anyway**. (Code signing requires a paid certificate.)

## Requirements

- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (to build) or the .NET 10 Desktop Runtime (to run)
- One or more Sonos players (S1 or S2) on the same local network

---

## Build & run

```powershell
git clone https://github.com/nhustak/HotSonos.git
cd HotSonos
dotnet build HotSonos.slnx
dotnet run --project src/HotSonos.App
```

The app starts in the system tray. **On first launch, Windows may prompt to allow HotSonos on the network** — allow it on *Private* networks. This is required for the speakers to push live now-playing/topology events back to the app (it runs a small local callback listener).

### Console harness
`src/HotSonos.Harness` is a small command-line tester for the core library:

```powershell
dotnet run --project src/HotSonos.Harness -- zones
dotnet run --project src/HotSonos.Harness -- --room "Living Room" favorites
dotnet run --project src/HotSonos.Harness -- --room "Living Room" shuffle
dotnet run --project src/HotSonos.Harness -- --ip 192.168.1.50 playpause
```

---

## How it works

- **Discovery** — SSDP `M-SEARCH` across every active network interface (important on multi-homed PCs where a single probe goes out the wrong adapter), then the full zone/group topology is resolved from any one responding player.
- **Control** — SOAP calls to each speaker on TCP port `1400` (`AVTransport`, `RenderingControl`/`GroupRenderingControl`, `ContentDirectory`, `ZoneGroupTopology`). Commands are routed to the group **coordinator**.
- **Library shuffle** — the `A:TRACKS` container is enqueued via `x-rincon-playlist:{coordinator}#A:TRACKS` in one call, then `SHUFFLE` mode + play-from-queue.
- **Live updates** — UPnP **GENA** event subscriptions (a local TCP HTTP listener receives `NOTIFY` callbacks; subscriptions auto-renew) for AVTransport (now-playing) and ZoneGroupTopology (grouping/drops).

### Project layout
| Project | Purpose |
|---|---|
| `HotSonos.Core` | Platform-agnostic Sonos UPnP client (discovery, control, favorites, events) |
| `HotSonos.App` | WPF tray app: hotkeys, settings, flyout, scheduler |
| `HotSonos.Harness` | Console tester for the core library |

---

## Notes & limitations

- HotSonos only **sends commands** — it can't cause speakers to fall out of sync. Persistent out-of-sync or a speaker repeatedly dropping is almost always a **wireless/signal issue** on that unit; wiring one nearby Sonos to ethernet (forming SonosNet) usually steadies the area. The "Restart fresh" and nightly re-sync features are the practical cure for a drifted group.
- The nightly re-sync only fires while the **PC is awake and HotSonos is running**; if the machine is asleep at the scheduled time it simply runs the next night.
- Playing a saved **Sonos Playlist** uses the same container-enqueue method as the library shuffle. (Empty playlists have nothing to play.)

---

## License

[MIT](LICENSE) © 2026 Nick Hustak. Provided as-is with no warranty. Not affiliated with Sonos, Inc.
