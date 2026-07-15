# HotSonos — Specification

> **Status**: Living product + roadmap spec (2026-07-15).  
> Prefer this file over chat history when requirements conflict.  
> Sections are marked **Shipped**, **Next**, or **Later** so agents do not implement the wrong phase by accident.

---

## 1. Overview

| | |
|--|--|
| **Platform** | Windows 10/11 x64 only |
| **Runtime** | .NET 10+ (WPF + WinForms tray) |
| **Site / brand** | [hotsonos.com](https://hotsonos.com) (domain registered; site TBD) |
| **Source** | https://github.com/nhustak/HotSonos (MIT) |

### Purpose
Control a **local** Sonos system from global hotkeys and the system tray — no Sonos cloud account, no OAuth, no internet required for control.

### Philosophy
- Tray-resident, instant, practical over feature-complete  
- **Local-only** control (UPnP/SOAP)  
- **Daily spin** stays sacred: whole-house shuffle of the *main* music library  
- **Mood music** (jazz, soundtracks, etc.) is opt-in via playlists — not mixed into daily shuffle  
- Future: agent-accessible library intelligence (tags, playlists, MCP) without turning HotSonos into a full DAM overnight  

### Primary use cases

| Priority | Use case |
|----------|----------|
| **P0 (shipped)** | Hotkey / tray: shuffle entire **daily** library to all speakers; play/pause, skip, volume |
| **P0 (shipped)** | Play Sonos favorites / saved playlists by hotkey or menu |
| **P0 (shipped)** | Wake-to-music, nightly re-sync, live topology / now-playing |
| **P1 (next)** | Stronger **playlist** workflow; daily vs mood library boundaries |
| **P2 (later)** | Local library index, file tags (tempo), MCP for agents |
| **P3 (later)** | Library management, recipes, health, creative filters |

---

## 2. Feasibility (Sonos local control) — Shipped foundation

Sonos speakers expose a local UPnP/SOAP server on **TCP 1400**, discoverable via SSDP (UDP 1900).

| Feature | Local mechanism |
|---------|-----------------|
| Play / Pause | `AVTransport` `Play` / `Pause` |
| Next / Previous | `AVTransport` `Next` / `Previous` |
| Play Favorite | Browse `FV:2` → `SetAVTransportURI` + `Play` |
| Play Playlist | Browse `SQ:` → `x-rincon-playlist:{uuid}#SQ:N` → queue + play |
| Shuffle Music Library | Browse `A:TRACKS`, client-side Fisher-Yates, batch `AddMultipleURIsToQueue`, `NORMAL` play mode |
| Volume | Per-member `RenderingControl` (group write often 803 with fixed-volume members) |
| Level all | Absolute `SetVolume` + unmute |
| Now playing | GENA AVTransport `LastChange` |
| Topology / drops | GENA ZoneGroupTopology |

**Rejected**: Sonos cloud Control API (OAuth + latency).  
**Rejected**: third-party Sonos NuGet — hand-rolled UPnP client.

### Local music libraries on Sonos
- Local files are first-class when they live on **Music Library share(s)** configured in the Sonos app (SMB).  
- Sonos supports **multiple** library folders/shares; they appear as one Music Library.  
- Streaming, radio, line-in, AirPlay, etc. do **not** require those shares.  
- **HotSonos policy**: daily shuffle targets the **configured daily library** (see §5). Mood collections must not pollute daily spin.

---

## 3. Architecture (current)

### Projects
| Project | Role |
|---------|------|
| `src/HotSonos.Core` | Platform-agnostic UPnP client (discovery, control, favorites, GENA) |
| `src/HotSonos.App` | WPF tray app: hotkeys, settings, flyout, wake, nightly |
| `src/HotSonos.Harness` | Console harness against live speakers |
| `tests/HotSonos.Core.Tests` | Offline unit tests (parsers / playability) |

### Core
- **`SonosDiscovery`** — SSDP per usable IPv4 interface; topology via `GetZoneGroupState`  
- **`SonosSoapClient`** — SOAP POST to `http://{ip}:1400{path}`  
- **`SonosController`** — high-level intents on a group coordinator  
- **`SonosEventSubscriber`** — GENA + local callback listener; renew via `PeriodicTimer`  

### App
- **`App.xaml.cs`** — single-instance, exclusive action gate, tray bootstrap  
- **`Infrastructure/`** — tray, hotkeys, startup, version, `AppLog`  
- **`Services/`** — `SonosManager`, `ConfigStore`, `WakeMusicService`  
- **`Windows/`** — Settings, now-playing flyout  

### Config & packaging
- Settings: `%LocalAppData%\HotSonos\settings.json`  
- Logs: `%LocalAppData%\HotSonos\logs\hotsonos-yyyyMMdd.log` (7-day retention)  
- Version: `Directory.Build.props` (CI/release tags override)  
- MSI: per-user, self-contained win-x64, WiX  

---

## 4. Shipped features (v1.x)

### System tray
- Custom icon; version in tooltip/menu  
- Menu: Open, refresh, fresh start, shuffle, transport, volume, level-all, rooms, favorites, offline line, diagnostics, exit  
- Double-click = shuffle library → all speakers  
- Optional Start with Windows (`--autorun` silent)  
- **Stop wake / volume ramp** when wake is active  

### Hotkeys (defaults)
| Action | Default |
|--------|---------|
| Shuffle Music Library → all speakers | Ctrl+Alt+F8 |
| Play / Pause | Ctrl+Alt+F9 |
| Previous / Next | Ctrl+Alt+F10 / F11 |
| Volume up / down / mute | Ctrl+Alt+↑ / ↓ / M |
| Level all / Fresh start / favorite slots | Unassigned |

### Music Library shuffle (daily primary)
1. Group all visible players under active coordinator  
2. Browse full `A:TRACKS` (paginated), client-side shuffle  
3. Clear queue; enqueue in batches of 16; play `NORMAL`  

Device `SHUFFLE` mode is **not** used (deterministic order for same queue content).  
Concurrent shuffle / Fresh Start: exclusive gate + Busy feedback.

### Favorites / playlists — Shipped baseline
- Four hotkey slots → active room/group  
- Favorites (`FV:2`): need playable `<res>`  
- Playlists (`SQ:`): play by **container id** (even if `<res>` empty/`file://`)  
- Browse paginated  

**Known gap (Next):** playlist UX and reliability polish; create/edit not shipped.

### Target zone
- Commands → group coordinator  
- Labels like Sonos app; tray switches active group  

### Now-playing flyout
- Art + title + artist + status; draggable, pinnable  
- Track-change vs action toggles; connectivity messages  

### Live speaker monitoring
- Topology GENA; offline indicator; rejoin on reconnect  
- No “just dropped” spam on first snapshot  

### Volume
- Group step + mute; level-all absolute %  
- Per-speaker sliders in Settings  

### Settings auto-refresh — Shipped
- Opening Settings runs **full discovery** in background (rooms, favorites, volumes), not volumes-only  

### Nightly re-sync
- Optional (default 03:00): regroup if nothing playing; optional reshuffle  
- PC must be awake + app running  

### Wake to music
- Days + time; **per-room** start  
- Start/end volume, step %, interval minutes  
- Source: shuffle library **or** favorite/playlist  
- Optional end-of-ramp: **whole house + full library shuffle**  
- Cancel: tray or volume hotkeys (no expand)  
- **Skip entirely if any group is Playing/Transitioning**  
- PC must be awake + app running  

### Diagnostics
- File + ring buffer; tray open log folder / copy diagnostics  

### Engineering constraints (shipped)
- Action gate for long library ops  
- GENA on ephemeral port (`IPAddress.Any`) — trusted LAN  
- No third-party logging package  

---

## 5. Product direction — Daily vs mood

### Daily spin
- Default mental model: “play my **normal** library everywhere, shuffled.”  
- Implementation today: Sonos `A:TRACKS` for whatever Sonos has indexed.  
- **Next**: allow configuring a **daily library root / container** so mood folders are not in daily shuffle even if on the same NAS.

### Mood collections
Examples: jazz, film scores, seasonal — **not** wanted in daily spin.  
Access model:

1. **Playlists** (primary) — Sonos `SQ:` / favorites; hotkey slots; wake source  
2. **Later** — tag-built playlists (“slow jazz”) from file metadata  
3. **Optional** — separate Sonos library folder/share for mood trees  

### Library roots (planned)

| Root | Role |
|------|------|
| **Sonos library path(s)** | Share/folder(s) Sonos indexes; FLAC/MP3 playable set; HotSonos daily + playlists |
| **Master library path** | Full archive (may include hi-res FLACs Sonos cannot play well); durable tags only |

Same NAS family is expected (e.g. `\\server\Music\Sonos\…` plus a master tree).  
**Do not** add master hi-res dump as a Sonos library share.

---

## 6. Next (near-term enhancements)

### 6.1 Playlist experience
- Reliable list/refresh of playlists + favorites in Settings/tray  
- Clear feedback when empty / non-playable  
- Prefer play-by-id for `SQ:` (already) — harden edge cases  
- Document / support mood playlists as first-class “modes” alongside shuffle  

### 6.2 Daily shuffle scope
- Setting: daily library scope (all indexed `A:TRACKS` vs specific container/path policy)  
- Goal: mood content accessible via playlist without entering daily shuffle  

### 6.3 Wake + playlists
- Already supports favorite/playlist source — ensure mood playlists work end-to-end for wake  

### 6.4 Spec/docs site
- Optional static site on **hotsonos.com** (download MSI, features, GitHub link)  

---

## 7. Later — Library intelligence & MCP

> Not scheduled for immediate implementation. Do not build unless the user asks for this phase.

### 7.1 Goals
- Agents (and the owner) can **see** the local music library and **metadata**  
- **Tag** tracks for dimensions not in standard tags (especially **tempo**: fast / medium / slow)  
- **Create playlists** of “this kind of music”  
- Optionally play them on Sonos  
- Tags durable in **files**, not only a fragile local DB  

### 7.2 Architecture split

```text
┌──────────────────────────────────────────┐
│ HotSonos MCP (loopback, agent-facing)    │
│ library search · tags · playlists · ops  │
│ thin Sonos control wrappers (optional)   │
└────────────┬───────────────┬─────────────┘
             │               │
             ▼               ▼
   ┌─────────────────┐  ┌──────────────────┐
   │ File library    │  │ Sonos UPnP       │
   │ scan + tag R/W  │  │ play / rooms     │
   │ FLAC + MP3      │  │ SQ: / queue      │
   └─────────────────┘  └──────────────────┘
```

- **Library visibility ≠ Sonos ContentDirectory** as the system of record.  
- Index the **filesystem** on the configured share(s).  
- Sonos remains transport + optional `SQ:` mirror.

### 7.3 Tagging
- **Formats in scope**: FLAC (Vorbis comments), MP3 (ID3v2 / TXXX). No emphasis on WAV/AIFF.  
- **Write tags into files** on the share (audio stream not re-encoded).  
- Suggested custom field: `HOTSONOS_TEMPO=slow|medium|fast` (+ optional `BPM`).  
- Standard fields: title, artist, album, track, genre, etc. (read/write when useful).  
- **SQLite** (or similar) under `%LocalAppData%\HotSonos\` is a **rebuildable cache** only; rescan restores meaning from files.  
- Cloud backup of the music tree is assumed acceptable risk for tag writes.

### 7.4 Sonos file vs master file
| Action | Behavior |
|--------|----------|
| Tag write | Always update **Sonos-library** file when that path is the working track |
| **Update master** | Option (default once linked): find twin in master tree and write the same tags |
| Match strategy | Content hash preferred; else artist+album+title+track (+ duration); optional relative-path suffix; manual link when ambiguous |
| Master offline | Write Sonos file; report master skip / queue |

### 7.5 Playlists (later)
| Step | Mechanism |
|------|-----------|
| Create from filter | Query cache (tempo, genre, artist, …) → ordered track list |
| Persist | App DB + optional M3U on share; and/or Sonos `SQ:` via UPnP `CreateObject` + add URIs |
| Play | Resolve paths to Sonos-playable URIs → queue / play group |
| Daily vs mood | Daily shuffle remains separate; mood = playlists |

Sonos UPnP **can** create/edit `SQ:` playlists in principle; treat as optional polish after local playlist + play works.

### 7.6 MCP (loopback)
Expose tools roughly like:

| Tool area | Examples |
|-----------|----------|
| Library | `library_search`, `track_get`, `library_rescan` |
| Tags | `track_set_tags` (`updateMaster` flag) |
| Playlists | `playlist_create`, `playlist_list`, `playlist_add` |
| Sonos | `sonos_play_playlist`, `sonos_shuffle_daily`, rooms/state (wrap existing) |
| Safety | dry-run flags on bulk writes |

Auth: localhost only. Return small result pages — never dump the whole library into chat.

### 7.7 Suggested implementation order (later)
1. Config: Sonos library root(s) + optional master root  
2. Scanner → SQLite cache (FLAC/MP3 tags)  
3. Read/write `HOTSONOS_TEMPO` (+ standard fields)  
4. Master match + optional dual write  
5. MCP search / get / set_tags  
6. Playlist create from filter + play on Sonos  
7. Optional Sonos `SQ:` create  
8. Optional BPM analysis to *suggest* tempo (never sole source of truth)  

---

## 8. Later — Cool MCP / product ideas (backlog)

> Explicitly **Later**. Nice-to-have once library + MCP exist. Not commitments.

### House / control recipes
- **“Set the house for X”** — room(s), volume, playlist or shuffle in one intent  
- **Wake / wind-down recipes** — compose wake + playlist + volume ramp from natural language  
- **Guest mode** — time-boxed volume cap + safe playlist  
- **Explain why it’s quiet** — offline speakers, empty playlist, wrong group (use logs + topology)  
- **House diff** — who’s grouped, volumes, now-playing per coordinator  

### Library intelligence
- **Tag gaps report** — missing tempo/BPM/genre; batch suggest-then-write  
- **Sonos vs master tag drift** — linked tracks that disagree  
- **Duplicates / near-duplicates** — same work, two rips  
- **What’s new on the share** — files added this week  
- **Never / rarely played** — only if optional play-history logging is enabled (privacy-sensitive; off by default)  

### Creative
- **Conversational playlist build** — “rainy soundtrack, instrumental, short cues”  
- **Continue this vibe** — seed from now-playing → expand by tags/artist/album  
- **Tempo lanes** — drive / dinner / focus from `HOTSONOS_TEMPO`  

### Ops / safety
- **Health check** — share reachable, index age, speakers offline, wake schedule next fire  
- **Dry-run everything** for bulk tag or playlist ops  
- **Approve queue** for destructive or bulk file writes  

### Explicitly deferred / out of scope for MCP era
- Full file manager (mass move/delete/reorganize) without strong confirm UX  
- Streaming-service accounts / cloud Control API  
- Non-Windows  
- Auto-tagging entire library without user confirm on bulk writes  
- Wake PC from sleep / Windows Task Scheduler  

---

## 9. Out of scope (general)

| Item | Notes |
|------|--------|
| Sonos cloud API | Rejected |
| Stereo-pair / advanced grouping editor | Not v1 |
| EQ, multi-alarm, snooze, fade-out sleep | Not planned |
| Code-signed MSI | Cost; SmartScreen may warn |
| Non-Windows platforms | — |

---

## 10. Engineering notes

- **Concurrency**: exclusive gate for shuffle / fresh start / long wake play phases  
- **GENA**: local callback; trusted LAN  
- **Tests**: Core parsers offline; Harness for live speakers  
- **Config today**: JSON settings; **later** library cache may be SQLite **in addition**, never as sole tag store  
- **Hand-rolled UPnP**; no Sonos NuGet  

---

## 11. Decisions log

| Decision | Choice |
|----------|--------|
| Control path | Local UPnP only |
| Daily primary action | Client-side full-library shuffle (scope to be refined) |
| Mood music | Playlists first; not mixed into daily shuffle |
| Favorites slots | 4 |
| Config | JSON `%LocalAppData%\HotSonos\settings.json` |
| Wake if already playing | Skip entirely |
| Future tags | Write into FLAC/MP3 on share; custom `HOTSONOS_TEMPO` |
| Future DB | Rebuildable index only |
| Future master | Optional dual-write when track linked |
| MCP | Loopback, later phase |
| Library management | Later; dry-run + confirm |

---

## 12. Document history

| Date | Note |
|------|------|
| 2026-06-14 | Initial draft / design |
| 2026-07 | Shipped v1.x features documented |
| 2026-07-15 | Roadmap: daily vs mood, library tags, master mirror, MCP, later backlog |
