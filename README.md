# HotSonos

[![build](https://github.com/nhustak/HotSonos/actions/workflows/build.yml/badge.svg)](https://github.com/nhustak/HotSonos/actions/workflows/build.yml)
[![latest release](https://img.shields.io/github/v/release/nhustak/HotSonos)](https://github.com/nhustak/HotSonos/releases/latest)
[![license](https://img.shields.io/github/license/nhustak/HotSonos)](LICENSE)

**Version 1.0.0.55** · [Releases](https://github.com/nhustak/HotSonos/releases) · [CI](https://github.com/nhustak/HotSonos/actions/workflows/build.yml) · [Spec / roadmap](spec.md)

Windows system-tray utility for controlling a Sonos system with global keyboard shortcuts. Open source ([MIT](LICENSE)), maintained by [Nick Hustak](https://github.com/nhustak).

HotSonos talks to your Sonos speakers entirely over the **local network** (UPnP/SOAP) — no cloud, no Sonos account, no internet round-trips. It lives in the system tray and gives you instant, global hotkeys for the things the Sonos apps make you click through: shuffle your whole library to every speaker, play/pause, skip, and whole-house volume — plus a live now-playing flyout, **tags as dynamic playlists**, and automatic speaker re-sync.

> Built for Windows 10/11 on .NET 10 (WPF). Works with Sonos S1/S2 players on the same LAN.

### Product direction
- **Today:** history-aware shuffle (**All**, **tag**, or **genre** from Control), transport/volume hotkeys, favorite slots (Sonos / tag / genre), wake-to-music, live topology, local library cache, flat tag catalog (`HOTSONOS_TAGS`), Control play list, Quick Tag / Quick Play, optional **hide genres** for simpler installs, loopback MCP for agents.
- **Settings UI:** left vertical nav — **Control · Hotkeys · Shuffle · Library · Tags · Wake · Options · MCP Debug**.
- **Next:** playlist create-from-filter + play (see **[spec.md](spec.md)** §0).
- **MCP:** with the tray app running: `http://127.0.0.1:42341/mcp` (devices, control, library search/tags/genres/master, play track/tag/genre, logs).

---

## Features

### 🔀 History-aware library shuffle
Groups speakers and builds a short random mix from your music library, leaves out songs you’ve already heard recently, and plays that list straight through. When the list is almost done, it adds another fresh batch the same way. That starts music quickly and keeps the day from repeating what you already listened to.

**Control → Shuffle** is no longer “always everything.” Use the **From** dropdown, then **Start shuffle now**:

| From | What plays |
|------|------------|
| **All · Music Library** | History-aware full-library shuffle (same as the shuffle hotkey / tray double-click default) |
| **Tag · …** | Shuffled HotSonos tag set (optional top-up into full library when enabled) |
| **Genre · …** | Shuffled tracks matching the standard Genre field (same top-up rules; hidden if genres are off) |

Also: **double-click tray icon** (configurable), **hotkey** (always **All**), Quick Play slot **1**, MCP `shuffle_library` / `play_tag` / `play_genre`. Optional **artist spacing** and queue/history parameters live under **Shuffle**.

**Hide genres for another user:** **Shuffle → Show genres in shuffle / play lists** — off removes genres from Control **From**, Control play list, Quick Play, and favorite-slot dropdowns (tags + All + Sonos stay).

**After a special play** (one track, tag, or genre queue): by default HotSonos **continues into full-library shuffle** when the special queue runs low. Turn that off under **Shuffle** if you want special plays to end cleanly.

#### Shuffle FAQ

**What does it do?**  
It builds a short random mix from your library, leaves out songs you’ve heard recently, and plays that list in order. When the list is almost empty, it adds another fresh batch.

**Why a small list instead of the whole library?**  
A short mix starts faster and is more reliable on Sonos. Dumping thousands of songs at once is slow and can be flaky. Small batches keep things snappy and let HotSonos keep the mix fresh as you listen.

**How does it avoid repeats?**  
As songs play **or you skip them (Next)**, HotSonos remembers them. When it builds the next batch, it leaves those out (by default, roughly the last couple of weeks). Top-up also won’t re-add tracks already put on the queue in the current shuffle session.

**Does it reshuffle what’s already lined up?**  
No. Songs already in the queue stay put. New songs only get added at the end.

**What happens when the queue is almost empty?**  
HotSonos quietly adds another random batch of unheard-recently songs so music keeps going without you doing anything.

**What if I’ve already heard most of my library?**  
If there aren’t enough “fresh” songs left, it loosens the “skip recent” rule so music can still play.

**Is this the same as a saved Sonos playlist?**  
No. This is a live daily mix for whole-house listening. Tags and Sonos favorites/playlists are better for intentional moods (“Dinner,” “Favs,” etc.).

### 🔄 Restart fresh (re-sync + reshuffle)
Re-discovers speakers, force-regroups them, and starts a new history-aware shuffle. Tray item, Control button, optional hotkey, MCP `fresh_start`.

### ⏯️ Transport & volume hotkeys
Play/pause, next, previous, volume up/down, mute, and level-all — from any app. Re-bindable under **Hotkeys**.

**Volume ±** uses **house logical** level (normal rooms with offset 0) for the toast and step base — not the group coordinator’s raw %, so a Port/Theater with a big amp offset never becomes “the volume.” Each room is written as `logical + room offset` (Port stays loud enough; Eras stay at house level). Steps stay snappy: one fast write for feedback, other rooms fan out in the background.

### 🔉 Level all speakers
One click (or hotkey) sets every speaker from a **house logical** percent (default 20%) and unmutes them. **Per-room offsets** (Speakers panel) add calibration for amp-fed Ports (e.g. logical 20 + Theater +60 → Port raw 80%). Same offsets apply on **Wake** absolute ramps.

### 🏷️ Tags as dynamic playlists
Flat tag catalog: each tag has an **opaque key** (stored in files) and a **renamable label** (UI only).

- Files store `HOTSONOS_TAGS=key1;key2` (FLAC Xiph / MP3 TXXX). Rename never rewrites files.
- **New install seed:** Slow, Medium, Fast, Dinner, Drive, Focus (editable on the **Tags** tab).
- **Library:** select tracks → click chips (or keys **1–9**) to toggle tags. Create/rename/reorder/delete on **Tags** only.
- **Play a tag:** Control list, Quick Play (**2–9**), favorite slot bound to a tag, or MCP `play_tag` — shuffles matching tracks, then (by default) top-ups into house shuffle.
- **Play a genre:** same places as tags, using the standard **Genre** field from your files (library cache after scan). Control **From** + play list, Quick Play, favorite slots, MCP `list_genres` / `play_genre`. Optional — hide via **Shuffle** for installs that don’t want genre UI.
- Search prefixes: free text, or `T:` title · `A:` artist · `TG:` tag · `F:` format (one prefix at a time).

> Legacy `HOTSONOS_TEMPO` is no longer a special case; leftover tempo tokens are migrated into the flat catalog where needed.

### 🎴 Quick Tag & Quick Play overlays
| Overlay | Default | What it does |
|---|---|---|
| **Quick Tag** | `Ctrl + Alt + T` | Tag the currently playing library track (keys 1–9 = catalog order). |
| **Quick Play** | `Ctrl + Alt + P` | **1** = library shuffle; **2–9** = tags, genres & Sonos playlists (same idea as Control Play). |

### ⭐ Favorite slots (1–6)
Each slot can be a **Sonos favorite/playlist**, a **HotSonos tag**, or a **library genre**, with its own hotkey. Same playback path as Control Play / `play_favorite_slot`.

### 🎮 Control page
- **From** dropdown (All / tag / genre) + **Start shuffle now** / Restart fresh, level-all, target room/group, **full-width speakers**.
- **Preferred house coordinator** — who should lead when you regroup the house (e.g. Office or Theater/Port); verified after join (★ COORD on Topology map).
- **Play tags, genres & Sonos playlists** — one-click play; list shares vertical space with speakers.
- Hide genres for other users under **Shuffle → Show genres in shuffle / play lists**.
- Layout keeps **labels + fields + buttons grouped left** (no stretch-to-far-right action buttons).

### 📚 Local library cache
- **Discover from Sonos** (share roots from `x-file-cifs` URIs)  
- SQLite cache of FLAC/MP3 metadata (format, bit depth, sample rate, bitrate)  
- **Sonos-unplayable** heuristic for hi-res / out-of-spec files  
- Optional **master mappings** (Sonos path → hi-res master root) with match + dual-write of tags; unmapped folders stay Sonos-only  
- **Daily mix folders**: which discovered library folders count as “All / house shuffle” (e.g. only `Sonos`, not Jazz/Christmas)  
- **Folder play**: each library folder is a shuffle mode (Control From / play list, Quick Play, favorite slots, MCP `list_library_folders` / `play_folder`); top-up stays in that folder  



- Paths / rescan / force re-read tags / search under **Library**  
- DB: `%LocalAppData%\HotSonos\library.db`

### 🎴 Live Now-Playing flyout
Album art, title, artist, state — GENA push updates. Draggable, pinnable; toggles under **Options**.

### 📡 Live speaker monitoring
Topology events: offline tray indicator, reconnect toasts, auto-rejoin active group, live room picker.

### ☀️ Wake to music
Scheduled start on a room, volume ramp, favorite or shuffle source, optional whole-house expand + shuffle. Skips if already playing. MCP `wake_now` / `wake_cancel`.

### 🖱️ Tray double-click
Configurable under **Options**: start shuffle (default), open Control, or open Library. Right-click still opens the full tray menu.

### 🤖 Loopback MCP
While the app runs with MCP enabled: `http://127.0.0.1:42341/mcp` — discovery, control, library, tags, play, logs. Live command log on **MCP Debug**. Register via `C:\Project\_mcp` if you use the multi-agent MCP hub.

**Library / tags (examples):** `discover_library_roots`, `get_library_config`, `get_library_status`, `library_rescan`, `library_search`, `library_get_track`, `list_tags`, `list_genres`, `tag_create`, `tag_rename`, `tag_delete`, `track_toggle_tag`, `track_set_tags`, `track_find_master`, `track_link_master`

**Control (examples):** `play_pause`, next/previous, volume/mute/level, `shuffle_library`, `play_library_track`, `play_tag`, `play_genre`, `resume_shuffle`, `fresh_start`, `play_favorite_slot`, `set_active_room`, wake tools

### Other
- Single-instance tray app; second launch activates the running window  
- Optional **Start with Windows**; nightly silent re-sync  
- Config: `%LocalAppData%\HotSonos\settings.json`  
- Play history (exclude set): `%LocalAppData%\HotSonos\play-history.json`  
- Play lifecycle events (debug): `%LocalAppData%\HotSonos\play-events.jsonl` · MCP `get_play_events`  

- Logs: `%LocalAppData%\HotSonos\logs`

---

## Default hotkeys

| Action | Default shortcut |
|---|---|
| Shuffle Music Library → all speakers | `Ctrl + Alt + F8` (also **tray double-click** by default) |
| Play / Pause | `Ctrl + Alt + F9` |
| Previous track | `Ctrl + Alt + F10` |
| Next track | `Ctrl + Alt + F11` |
| Volume up | `Ctrl + Alt + ↑` |
| Volume down | `Ctrl + Alt + ↓` |
| Mute / Unmute | `Ctrl + Alt + M` |
| Quick tag overlay | `Ctrl + Alt + T` |
| Quick play overlay | `Ctrl + Alt + P` |
| Level all / Restart fresh / favorite slots 1–6 | unassigned (set your own) |

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
- Library scan/tag write: this PC needs SMB access (read for scan, write for tags) to the music share Sonos indexes  
- Optional master dual-write: SMB write under a **mapped** master root for that Sonos path  


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
| `HotSonos.App` | Tray app: hotkeys, settings, library, tags, MCP, wake |
| `HotSonos.Harness` | Console tester |
| `HotSonos.Core.Tests` | Offline unit tests |

Version is single-sourced in `Directory.Build.props`; release tags override with `-p:Version=…`.

---

## How it works

- **Discovery** — SSDP across interfaces; topology from any responding player.  
- **Control** — SOAP on TCP **1400** to group coordinators.  
- **Shuffle** — browse `A:TRACKS`, exclude recently **played** tracks, short queue (~80 default), auto **top-up** near end, play in `NORMAL` mode.  
- **Tags** — catalog in settings; keys written into audio files as `HOTSONOS_TAGS`; SQLite is a rebuildable cache only.  
- **Tag / genre / one-shot play** — client builds a queue from the library cache; optional continue into full-library top-up (`ContinueLibraryShuffleAfterSpecialPlay`).  
- **Control shuffle From** — `ControlShuffleSource` = `all` | `tag:{key}` | `genre:{name}`; genres gated by `ShowGenresInPlaySources`.  
- **Library** — filesystem scan under discovered UNC roots; optional master match for dual-write.  
- **MCP** — Kestrel loopback host inside the tray process.  
- **GENA** — local listener for now-playing, topology, and RenderingControl volume/mute (coordinator).  
- **Volume** — house logical % for ±; per-room offsets on absolute write (Level all / Wake / ±); Speakers sliders show raw Sonos %.

---

## Notes & limitations

- Speakers out of sync is usually Wi‑Fi; Restart fresh / nightly re-sync help.  
- Nightly re-sync and wake need the **PC awake** with HotSonos running.  
- Sonos does not reliably report “can’t play this file”; unplayable flags are **format heuristics**.  
- Shuffle history only reshapes the queue at **rebuild/top-up**, not mid-queue.  
- Tag write needs **write** SMB access from this PC to the Sonos share (and master root if dual-write is on).  
- GENA callback is for a **trusted home LAN**.

---

## Changelog

### 1.0.0.55
- **House-logical volume ±:** toast and step base from offset‑0 rooms (never Port/coordinator raw %); each room written as `logical + offset` so amp-fed Theater stays usable without driving the house number  
- **Snappy volume path:** cache logical level, await one reference write, fan out others; SetVolume-only on ± (no extra unmute SOAP per step)  
- **Preferred house coordinator** with become-standalone + regroup verify; Topology **★ COORD** labeling  
- **Live Speakers list** refresh on volume/mute writes + RenderingControl GENA  
- **Failure diagnostics** (tray/hotkey/MCP dump), keep-alive restarter hardening, now-playing **source** labels (library host / stream)  
- GENA + poll product path restored after isolation experiments; tray process keep-alive window  

### 1.0.0.30 – 1.0.0.54
- Stability work: startup races, GENA renew, restarter, isolation experiments, then product features restored (see git history on `master`)  

### 1.0.0.29
- **Control shuffle / fresh start:** busy UI feedback, disable double-click while queue builds  
- **Level all** refreshes speaker list after apply; **Refresh volumes** on Speakers panel  
- Volume path polish (coordinator-first steps, non-blocking AppLog writes)  

### 1.0.0.28
- **Per-room volume offset** for Level all / Wake (amp/Port calibration); relative volume stays raw  
- **Topology monitor** (optional, default OFF) + map UI; volume hotkeys fast (coordinator-first)  
- Volume steps no longer queue behind long actions (no walk-to-100% lag stack)  
- Tray left-click / open brings main window to front when already open  

### 1.0.0.27
- **Control shuffle From:** dropdown (All · tags · genres) before **Start shuffle now** — not always full library  
- **Shuffle → Show genres…** option to hide genres from all play/shuffle pickers (other-user installs)  
- Hotkey shuffle remains full library (All)

### 1.0.0.26
- **Shuffle by genre:** play/shuffle all tracks with a standard Genre label (from library cache)  
- Genres appear in Control Play list, Quick Play (2–9), favorite-slot dropdowns, MCP `list_genres` / `play_genre`  
- Same optional top-up into full library shuffle as tag play  

### 1.0.0.25
- **Control UX:** speakers full-width, grow with window and share height with Play list; mute column no longer clipped  
- **Library UX:** removed inline “Add tag” (catalog only on **Tags** tab); search row is **Search** + field + **Go** / **Browse** grouped left  
- **Forms UX:** entry rows keep label + field + actions left-aligned (Tags add row, favorites, hotkeys, MCP header, Control refresh)

### 1.0.0.21 – 1.0.0.24
- **Quick Play** overlay (`Ctrl+Alt+P`): 1 = library shuffle, 2–9 = tags & Sonos playlists  
- **Control Play** list: tags + Sonos favorites/playlists with one-click Play  
- Favorite slots **1–6**: each may be Sonos *or* tag; tray **double-click** = shuffle / Control / Library  
- **Continue into library shuffle** after one-shot / tag / special play (default on; Shuffle settings)  
- Control Start shuffle button; layout polish for less wasted scroll space  

### 1.0.0.20
- MCP **`play_library_track`**, **`play_tag`**, **`resume_shuffle`**  
- Tag play shuffles the set; auto top-up can continue into full library shuffle  

### 1.0.0.18
- **Flat tag catalog** (auto key + renamable label); files use `HOTSONOS_TAGS`  
- **Tags** maintenance tab (add / rename / reorder / delete + purge from files)  
- Library search prefixes `T:` / `A:` / `TG:` / `F:`  
- New-install starter tags: Slow, Medium, Fast, Dinner, Drive, Focus  
- MCP tag catalog tools: `list_tags`, `tag_create`, `tag_rename`, `tag_delete`, `track_toggle_tag`  

### 1.0.0.10
- Quick-tag overlay and library chip tagging path (evolved into flat catalog above)  

### 1.0.0.9
- **History-aware shuffle**: short queues, hard-exclude played tracks, auto top-up near end, artist spacing; Settings under **Shuffle** (clear history, all parameters)
- **Library intelligence**: discover roots from Sonos, SQLite FLAC/MP3 cache, format / Sonos-unplayable flags, write tags (`track_set_tags`)
- **UI**: left vertical navigation
- **MCP**: library tools + control tools; live MCP command log
- Play history file: `%LocalAppData%\HotSonos\play-history.json`
- Master library match + optional dual-write (post-1.0.0.9 on `master`)

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
