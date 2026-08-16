# HotSonos — Specification

> **Status**: Living product + roadmap spec (2026-07-15).  
> Prefer this file over chat history when requirements conflict.  
> Sections are marked **Shipped**, **Next**, or **Later** so agents do not implement the wrong phase by accident.  
> **Agents: read §0 (progress) first after any context compression.**

---

## 0. Implementation progress (agent handoff)

> Update this section **in the same task** whenever library-plan or MCP work advances. This is the resume point after context compression.

### Crash isolation (`crash1`) — 2026-08-14

Numbered re-land of `stable/aug5-topology` commit `f6a3148` (1.0.0.70) onto hours-stable `.59` (`8543e20`). Version = `1.0.0.59-N` / Assembly `1.0.59.N`.

| # | Version | Item | Status |
|---|---------|------|--------|
| **1** | `1.0.0.59-1` | GENA forced off (poll-only; `HOTSONOS_GENA=1` re-enables) | **DONE** (overnight OK) |
| **2** | `1.0.0.59-2` | MCP `ManualHostLifetime` + Kestrel limits | **DONE** (overnight OK) |
| **3** | `1.0.0.59-3` | Do not force `McpEnabled=true` on startup | **DONE** (overnight OK) |
| **4** | `1.0.0.59-4` | AppLog lifecycle / `Before` last-action / heartbeat split | **DONE** (overnight OK) |
| **5** | `1.0.0.59-5` | CrashDumpBootstrap + `last-alive` | **DONE** — pid 29536 up ~24h (started 2026-08-13 08:56) |
| **6** | `1.0.0.59-6` | Keep-alive close-cancel + SessionEnding / extra exit-lifecycle | **RAD** — ladder base; patches 6.7–6.12 on top |
| **7** | `1.0.0.59-7` | Poll 5s→10s + poll breadcrumbs | Pending |
| **8** | `1.0.0.59-8` | SSDP serialize send/recv | Pending |
| **9** | `1.0.0.59-9` | Full ChannelMapSet on members | Pending |
| **10** | `1.0.0.59-10` | SubGain | Pending |
| **11** | `1.0.0.59-11` | Safer regroup | Partial — pause all playing coordinators for the join; block auto-recover Next/Play during hold; resume after unless caller starts a new shuffle. Version not bumped (ladder still on #6). |
| **12** | `1.0.0.59-12` | Rebuild-bond | Pending |
| **13** | `1.0.0.59-13` | MainWindow topology UI | Pending |
| **14** | `1.0.0.59-14` | MCP tools expansion | Pending |
| **15** | `1.0.0.59-15` | Flyout art log extras | Pending |
| **16** | `1.0.0.59-16` | GENA renew Trace-only | Pending |

Master `1.0.0.60` / `1.0.0.61` (stale-queue reshuffle, fail-fast regroup) stay **off** this ladder until `.70` items are isolated.

**Patch notes on #6 (crash1):** `6.11` regroup preserves queue (skip BecomeStandalone when already coordinator). `6.12` Library grid ⚡ **Play now** — `AddURIToQueue` EnqueueAsNext + Seek TRACK_NR; does **not** `RemoveAllTracksFromQueue` (shuffle remainder stays).

### Snapshot (2026-07-28)

| Item | State |
|------|--------|
| **Git HEAD** | See git log |
| **App version** | `1.0.0.29` |
| **MCP endpoint** | `http://127.0.0.1:42341/mcp` (tray app must be running; enabled by default) |
| **Main window** | Tabs: Control · Hotkeys · Shuffle · Library · Tags · Wake · Options · Topology · MCP Debug |
| **Play sources** | Control **From**: All / tag / genre · play list · Quick Play · slots 1–6 · MCP; genres optional (`ShowGenresInPlaySources`) |
| **Next product slice** | **Library groups** (§5.1 / §6.2) — path modes; no group hotkeys |
| **User paths** | Prefer **Discover from Sonos**; tag write needs SMB **write** on this PC |
| **Library DB** | `%LocalAppData%\HotSonos\library.db`; `master_path` + `custom_tags` JSON |

### Library intelligence plan (§7.7) — checklist

| Step | Status | Notes |
|------|--------|--------|
| **1. Config: Sonos library root(s) + optional master root** | **DONE** | Settings + MCP; discover from Sonos |
| **2. Scanner → SQLite cache (FLAC/MP3 tags)** | **DONE** | TagLib read; rescan/search |
| **2b. Audio props + Sonos-unplayable flag** | **DONE** | Format heuristic + UI/MCP filter |
| **3. Read/write tags (+ standard fields)** | **DONE** | Flat tags → `HOTSONOS_TAGS` keys; MCP `track_toggle_tag` / `track_set_tags` |
| **4. Master match + optional dual write** | **DONE** | `LibraryMasterMatcher`; `updateMaster`; find/link master |
| **4b. Flat tag catalog + quick-tag** | **DONE** | Catalog `Tags` (auto key + label); no kinds/presets; Library chips + Ctrl+Alt+T; rename label without file rewrite |
| **5. MCP polish** | Partial | search/get/set_tags/master/presets shipped |
| **6. Playlist create from filter + play on Sonos** | **NEXT** | After multi-tag filters are in daily use |
| **7. Optional Sonos `SQ:` create** | Pending | |
| **8. Optional BPM analysis (suggest only)** | Pending | Never sole source of truth |

### Step 1 — what landed (files)

| Area | Location |
|------|----------|
| Model | `src/HotSonos.App/Models/AppSettings.cs` — `SonosLibraryRoots` (`List<string>`), `MasterLibraryRoot` (`string?`); cleaned in `EnsureShape()` |
| Settings UI | `MainWindow.xaml` + `.xaml.cs` — section **Music library paths**; multiline Sonos roots; master single line; save via `SplitLibraryRoots` |
| Persist | Existing `ConfigStore` → `%LocalAppData%\HotSonos\settings.json` (JSON property names as C# properties) |
| MCP | `HotSonosDebugTools.cs` — `get_library_config`; `get_settings_summary` includes `sonosLibraryRoots` + `MasterLibraryRoot` |
| Docs | this file, `Agents.md` (tool list), `README.md` (library roots bullet) |

### Step 2 — what landed (files)

| Area | Location |
|------|----------|
| Packages | `Microsoft.Data.Sqlite` 10.0.10, `TagLibSharp` 2.3.0 on `HotSonos.App` |
| Models | `Library/LibraryTrack.cs` — track row + `LibraryStatus` |
| DB | `Library/LibraryDb.cs` — schema, upsert, prune missing, search, meta |
| Tags (read) | `Library/LibraryTagReader.cs` — FLAC/MP3; standard fields + `HOTSONOS_TEMPO` if present |
| Service | `Library/LibraryService.cs` — background rescan, skip unchanged, auto-scan if roots set & empty cache |
| App wire | `App.xaml.cs` — create/dispose `LibraryService`; inject into MCP + MainWindow |
| Settings UI | Rescan / Force re-read tags buttons + live status line |
| MCP | `discover_library_roots`, `get_library_status`, `library_rescan` (auto-discover if empty), `library_search`, `library_get_track` |
| Roots source | Sonos ContentDirectory `A:TRACKS` → parse `x-file-cifs://` → UNC share roots (`SonosController.DiscoverMusicLibraryRootsAsync`) |

### UI — MCP Debug + Library tabs (2026-07-15)

| Area | Location |
|------|----------|
| Activity log | `Mcp/McpActivityLog.cs` — ring of tool calls (args/result/duration); all tools wrap `Run`/`RunAsync` |
| Main window | `MainWindow.xaml` — enlarged; **TabControl**: Settings / Library / MCP Debug |
| Library tab | Search/browse grid + status; fills from UI search **or** MCP `library_search` / `library_get_track` |
| MCP tab | Live command list + detail pane; clear; copy endpoint; auto-scroll |
| Tray | **Library…**, **MCP Debug…** open window on the right tab |

### Policy reminders for next steps

- **Do not** treat Sonos ContentDirectory as SoT for library intelligence — filesystem on configured roots.  
- **Do not** add master hi-res dump as a Sonos Music Library share.  
- Tags live in **files**; SQLite is **rebuildable cache only**.  
- Daily shuffle remains Sonos `A:TRACKS` until §6.2 / later scoping.  
- Shuffle history: GENA + **Next/skip** both mark tracks excluded (~14d); top-up also excludes tracks already enqueued this session.  
- MCP is loopback only; register tools via `C:\Project\_mcp\mcp-servers.json` + `sync-mcp.ps1` (server list), but HotSonos tools are live from the running app.  
- Commit only when the user asks; steps 1–2 are ready to commit when they do.  
- NuGet audit: transitive `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 may warn NU1903 until Microsoft.Data.Sqlite ships a newer native bundle — local DB only, not network-facing.

### Step 3 — tag write (files)

| Area | Location |
|------|----------|
| Writer | `Library/LibraryTagWriter.cs` — FLAC Xiph `HOTSONOS_TEMPO`; MP3 ID3v2 TXXX; optional title/artist/album/genre/track/year/bpm |
| Service | `LibraryService.SetTags` — path must be under `SonosLibraryRoots`; dry-run; upsert cache after save |
| MCP | `track_set_tags` (`path`, `tempo`, standard fields, `dryRun`) |
| UI | Library tab: select row → tempo combo → **Set tempo on selection** |

### Step 4 — master match + dual-write (files)

| Area | Location |
|------|----------|
| Matcher | `Library/LibraryMasterMatch.cs` — linked → relative path → alt ext → path suffix → filename / metadata score |
| Cache | `LibraryDb` `master_path` column (preserved across rescans); `SetMasterPath` |
| Service | `LibraryService.SetTags(..., updateMaster)`; `FindMasterMatch`; `LinkMaster` |
| MCP | `track_set_tags` `updateMaster` (default true); `track_find_master`; `track_link_master` |
| UI | Library tab set tempo dual-writes when master root set; status shows master result |

Match notes: content hash skipped (master often different encode/hi-res). Ambiguous / offline → Sonos write still succeeds; master reported as skip. Filename walk is scoped (artist/album) then time-budgeted (5s) so a large/slow master share cannot freeze the tray app.

### Immediate next work (when asked)

**Step 6** (or step 5 polish): playlist create from filter + play on Sonos; further MCP polish as needed.

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
| **P1 (shipped)** | Loopback **MCP** debug/ops + **control** tools (see §4); config in `C:\Project\_mcp` |
| **P2 (in progress)** | Library intelligence: **steps 1–4 done** (uncommitted locally); next playlists / MCP polish (§0, §7.7) |
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
| Shuffle Music Library | Browse `A:TRACKS`; **short queue (~80)**; hard-exclude tracks **played or skipped** (~14d); top-up also skips **session-enqueued** tracks; **auto top-up** near queue end; artist spread; `NORMAL` |
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
- **Topology monitor** (`TopologyMonitorEnabled`, default **OFF**):  
  - Topology tab checkbox **Monitor ON** — turn on only when debugging  
  - When OFF: light zone parse only (no bonded-Sub graph, no event JSONL spam, no map rebuild on every GENA push)  
  - When ON: full map + `topology-events.jsonl` + MCP topology tools  
- Volume ± hotkeys never wait on the shared action gate  
- **KeepHouseGrouped** (default off): optional auto-regroup  




### Volume
- Group step + mute; level-all absolute %  
- Per-speaker sliders in Settings (raw Sonos %)  
- **Per-room volume offset** — additive % for amp/Port calibration (e.g. Media Room +60 so house level 20 → 80 on that port). Applied on **Level all** and **Wake** absolute levels only; not on ± volume hotkeys or manual sliders. Stored as `RoomVolumeOffsets` in settings; UI: Control → Speakers → **Off** field.  

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

### Loopback MCP (debug / ops) — Shipped baseline
- Hosted **inside** the tray process (not a separate executable)
- Default: **enabled**, `http://127.0.0.1:42341/mcp` (port configurable; restart after change)
- Settings: enable + port; tray menu copies the endpoint
- **Discovery / debug:** `get_status` (includes `deviceListPopulated`), `get_discovery_state`, `list_groups`, `list_zones`, `list_offline`, `refresh_devices`, `get_speaker_volumes`, `get_now_playing`, `list_favorites`, `get_settings_summary`, `get_logs`, `get_log_directory`
- **Control:** `play_pause`, `next_track`, `previous_track`, `volume_up`, `volume_down`, `mute_toggle`, `level_volumes`, `shuffle_library`, `fresh_start`, `play_favorite_slot`, `set_active_room`, `wake_now`, `wake_cancel`
- **Library config + cache (steps 1–2):** `get_library_config`, `get_library_status`, `library_rescan`, `library_search`, `library_get_track`; roots also on `get_settings_summary`
- **Library tags + master (steps 3–4):** `track_set_tags` (`updateMaster`, `dryRun`), `track_find_master`, `track_link_master`
- Playlists remain **Later** (§7 steps 6+)

### Engineering constraints (shipped)
- Action gate for long library ops  
- GENA on ephemeral port (`IPAddress.Any`) — trusted LAN  
- No third-party logging package  

---

## 5. Product direction — Daily vs mood

### Daily spin
- Default mental model: “play my **normal** library everywhere, shuffled.”  
- Implementation today: Sonos `A:TRACKS` for whatever Sonos has indexed (mood folders included if Sonos indexes them).  
- **Agreed direction (§5.1):** daily = path-scoped **group mode**, not “everything Sonos knows.”

### Mood collections
Examples: Christmas, soundtracks, jazz corner — **not** wanted in daily spin, but playable on demand as a **mode**.  
Access model (target):

1. **Library groups (path modes)** — primary for folder-organized mood vs daily (**Next**, §5.1 / §6.2)  
2. **Tags** — cross-cutting across folders (Favs, Slow, Drive) — **shipped**  
3. **Genres** — standard file Genre field — **shipped** (optional in UI)  
4. **Sonos playlists / favorites** — intentional lists — **shipped**

### Library roots = one pool

| Root | Role | Status |
|------|------|--------|
| **Sonos library path(s)** | Share/folder(s) Sonos indexes; FLAC/MP3 playable set | **Configured** (`SonosLibraryRoots`); discover from Sonos |
| **Master library path(s)** | Hi-res archive(s) for dual-write tags | **Per Sonos path** via `MasterLibraryMappings` (legacy `MasterLibraryRoot` migrates on load) |

- Treat **all Sonos roots as one big pool** for grouping and play. Root count does not define modes.  
- **Do not** add master hi-res dump as a Sonos library share.  
- Paths alone do not yet change shuffle — still full `A:TRACKS` until groups ship.

### 5.1 Library groups (path modes) — design locked (2026-07-28)

> **Implement when asked.** Not started.

#### Model
- **Pool** = union of all configured Sonos library roots.  
- **Group** = `{ label, path prefix(es) under the pool }`.  
  - Default discovery: **top-level folders** under the pool become candidate groups.  
  - A group may later list **multiple paths** under the same pool (merge folders); v1 can be one folder = one group.  
- **Daily** = special default mode: house mix = pool **minus** groups marked exclude-from-daily (or explicit include list — prefer **exclude** mood folders when most of the tree is daily).  
- **Cross-cuts:** tags/genres stay independent of groups (Favs across Daily + Christmas, etc.).

#### Player “mode”
- Picking a group (or Daily) **sets the active shuffle scope** and builds a history-aware short queue from the **library cache** (path filter + Sonos-playable), same family as tag/genre play.  
- Sonos still indexes everything; HotSonos only enqueues the active slice.  
- Top-up while in a mode stays **inside that mode** (not “bleed into full library” unless user later opts in).

#### UI (v1) — no group hotkeys
| Surface | Behavior |
|---------|----------|
| **Control tab** | Mode picker: **Daily** + named groups (and keep existing tag/genre/Sonos play where useful). Start shuffle / current mode clear. |
| **Quick Play overlay** | Groups available as a **list / pull-down (or listed rows)** — pick mode and go. **No dedicated hotkeys per group** (excessive). Digits can remain for pinned sources if needed; groups need not burn 2–9. |
| **Settings / Library** | Discover/edit groups; mark exclude-from-Daily; refresh folder list after rescan. |
| **Hotkey shuffle / tray double-click default** | **Daily** mode (not unscoped All), once groups ship. |

#### Explicitly out of v1
- Per-group global hotkeys  
- Timed auto-switch (“Christmas for 3 hours then Daily”) — **phase 2 / later**, do not design further until v1 is shipping  
- Auto-creating Sonos `SQ:` playlists per folder  

#### MCP (when built)
- `list_library_groups`, `play_group` / `set_shuffle_mode` (names TBD) — optional with UI.

---

## 6. Next (near-term enhancements)

### 6.1 Playlist experience
- Reliable list/refresh of playlists + favorites in Settings/tray  
- Clear feedback when empty / non-playable  
- Prefer play-by-id for `SQ:` (already) — harden edge cases  

### 6.2 Library groups + daily scope (**primary Next product slice**)
- Implement §5.1: pool + groups + Daily exclude + Control mode + Quick Play group list  
- Goal: mood folders never pollute daily spin; one control action plays only that group  
- Prefer library-cache path filter over raw full `A:TRACKS` for scoped modes  

### 6.3 Wake + playlists
- Already supports favorite/playlist source — ensure mood playlists work end-to-end for wake  
- Later: wake source = Daily or a named group  

### 6.4 Spec/docs site
- Optional static site on **hotsonos.com** (download MSI, features, GitHub link)  

### 6.5 Later (not now)
- Timed mode swap (play group A for N hours, then group B / Daily)  
- Multi-path group editor polish if v1 is single-folder only

---

## 7. Later — Library intelligence (extends MCP)

> Debug/ops MCP is **shipped** (see §4 Loopback MCP). This section is the **library/tag/playlist** expansion — do not build unless the user asks for this phase.

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
│ debug/ops (shipped) + library (later)    │
│ tags · playlists · thin Sonos control    │
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

### 7.7 Suggested implementation order

> Canonical checklist with live status lives in **§0**. Keep both in sync when advancing steps.

1. ~~Config: Sonos library root(s) + optional master root~~ **Done**  
2. ~~Scanner → SQLite cache (FLAC/MP3 tags)~~ **Done** — TagLib read, `library.db`, rescan UI + MCP search/status  
3. ~~Write `HOTSONOS_TEMPO` (+ standard fields); MCP `track_set_tags`~~ **Done**  
4. ~~Master match + optional dual write~~ **Done** — `updateMaster`, find/link master tools  
5. Polish MCP search / set_tags / master (partial)  
6. **NEXT** — Playlist create from filter + play on Sonos  
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
- **Config today**: JSON settings at `%LocalAppData%\HotSonos\settings.json`  
- **Library roots (step 1)**: `SonosLibraryRoots`, `MasterLibraryMappings` (and legacy `MasterLibraryRoot`) in same JSON  
- **Library cache (step 2)**: SQLite `%LocalAppData%\HotSonos\library.db` via `Microsoft.Data.Sqlite` + TagLib# — **rebuildable only**, never sole tag store  
- **Hand-rolled UPnP**; no Sonos NuGet  
- **Build note**: Debug/Release build fails with file lock if tray `HotSonos.exe` is running — stop process, build, restart  
- **MCP registry**: `C:\Project\_mcp\mcp-servers.json` entry `hotsonos` → sync with `sync-mcp.ps1` (Grok included)  

---

## 11. Decisions log

| Decision | Choice |
|----------|--------|
| Control path | Local UPnP only |
| Daily primary action | Client-side full-library shuffle (scope to be refined) |
| Mood music | Playlists first; not mixed into daily shuffle |
| Favorites slots | 4 |
| Config | JSON `%LocalAppData%\HotSonos\settings.json` |
| Library roots | `SonosLibraryRoots[]` + `MasterLibraryMappings[]` (Sonos path → master); legacy `MasterLibraryRoot` migrates |
| Library cache | SQLite `library.db`; skip unchanged by size/mtime; FLAC/MP3 only |
| Wake if already playing | Skip entirely |
| Tags | Read + write `HOTSONOS_TEMPO` / standard fields (step 3) |
| Master dual-write | Optional (default on) when twin matched/linked under the **mapped** master root for that Sonos path |
| MCP | Loopback debug/ops + control + library status/search/tags/master |
| Library management | Later; dry-run + confirm |

---

## 12. Document history

| Date | Note |
|------|------|
| 2026-06-14 | Initial draft / design |
| 2026-07 | Shipped v1.x features documented |
| 2026-07-15 | Roadmap: daily vs mood, library tags, master mirror, MCP, later backlog |
| 2026-07-15 | Library plan step 1: configure Sonos + optional master roots (Settings + MCP) |
| 2026-07-15 | Added §0 agent handoff (progress checklist, files, next=scanner); pre-compression snapshot |
| 2026-07-15 | Library plan step 2: TagLib scanner → SQLite cache; rescan UI; MCP status/search/get |
| 2026-07-15 | Main window tabs: Library results + MCP Debug command log; tray shortcuts |
| 2026-07-15 | Library roots discovered from Sonos A:TRACKS (x-file-cifs), not manual-only |
| 2026-07-15 | Library audio props + Sonos-unplayable heuristic; GENA cross-check on track change |
| 2026-07-15 | Step 3: write HOTSONOS_TEMPO + standard tags to FLAC/MP3; MCP track_set_tags |
| 2026-07-16 | Step 4: master match + dual-write; track_find_master / track_link_master; master_path cache |
| 2026-07-17 | History-aware library shuffle: deprioritize recent plays/serves, queue cap 500, artist spread |
| 2026-07-19 | Shuffle v2: short queues, hard-exclude played only, auto top-up near end (history applies at rebuild) |
| 2026-07-26 | Skip (Next) writes play history via GetPositionInfo; top-up excludes session-served URIs (no re-queue of already-lined-up tracks) |
| 2026-07-26 | Play lifecycle log: started/skipped/paused/resumed → play-events.jsonl + AppLog + MCP get_play_events |
| 2026-07-28 | Library groups design locked §5.1: one pool, path groups, Daily exclude, Control + Quick Play (no group hotkeys); timed swap later |
| 2026-07-28 | Master dual-write: `MasterLibraryMappings` (Sonos path → master root); unmapped paths Sonos-only; legacy single root migrates |
| 2026-07-28 | Discover library roots: one root per top-level folder under the SMB share (Jazz, Sonos, Seasonal…) — not collapsed share root |
| 2026-07-28 | Daily mix folders (`DailyLibraryRoots`): All/hotkey shuffle scoped to checked library folders; multi-root defaults to …\Sonos |
| 2026-07-28 | Folder play modes: Control From/list, Quick Play, fav slots, MCP play_folder; top-up stays in folder |
