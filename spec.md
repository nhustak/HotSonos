# HotSonos — Specification

> **Status**: Living product + roadmap spec (2026-07-15).  
> Prefer this file over chat history when requirements conflict.  
> Sections are marked **Shipped**, **Next**, or **Later** so agents do not implement the wrong phase by accident.  
> **Agents: read §0 (progress) first after any context compression.**

---

## 0. Implementation progress (agent handoff)

> Update this section **in the same task** whenever library-plan or MCP work advances. This is the resume point after context compression.

### Promoted to master — 2026-08-18

`crash1` is **`master`** as **`1.0.0.70`**. Old master `1.0.0.61` remains on tag `v1.0.0.61` (SonosNet-era fail-fast; not this line).

### Crash isolation (`crash1`) — 2026-08-14

Numbered re-land of `stable/aug5-topology` commit `f6a3148` (old 1.0.0.70) onto hours-stable `.59` (`8543e20`). This line is now product **`1.0.0.70`**.

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

Old master `1.0.0.60` / `1.0.0.61` (fail-fast regroup) were **not** brought onto this line.

**Patch notes on #6 (crash1):** `6.11` regroup preserves queue. `6.12` Library ⚡ Play now. `6.13–6.14` Library **From** / Daily path scope. `6.15` fix folder/Daily path LIKE (`\%` was literal % under ESCAPE — zero hits for scoped search). `6.16` Options checkbox for GENA (`HOTSONOS_GENA=1` still overrides). `6.17` GENA default on for new installs. `6.18` auto-recover reshuffles a stale leftover library queue near track 1 instead of Play. `6.19` Debug tab; Force auto-recover marks the queue stale, seeks track 1, Stops, then reshuffles.

**1.0.0.71:** shuffle no longer drops 14-day play history when the Daily folder has fewer than ~40 unheard tracks (that was re-queuing yesterday). Empty unheard pool is the only case that reuses the full folder.

**1.0.0.72:** when the 14-day unheard pool is short, slide in the oldest plays first; never replay a track heard in the last 24 hours while any older track exists.

**Top-up:** a top-up never appends a track that is still waiting (enqueued this session and not played since). With a folder of about 50 tracks (queue 80, top-up 60 at 4 remaining) the first batch queues the folder and the top-up does not append those 4 again. When nothing unheard is left, the top-up replays tracks that have been heard and are not still waiting, oldest first. If every in-scope track is still waiting, it appends nothing. `ShuffleExcludePlayed` only controls recently heard tracks. A fresh shuffle still replaces the queue and, when every in-scope track was heard in the last 24 hours, still reuses that whole scope.

**1.0.0.73:** MCP **`queue_now`** first landed; 1.0.0.73 still sought and played (Play now). Wrong.

**1.0.0.74:** **`queue_now`** only pushes up-next: insert after the current track, no seek, no play, no shuffle clear. Not a refresh. Library ⚡ remains **Play now** (insert + seek + play).

**1.0.0.75:** **"Play X" keeps the queue.** `play_library_track` puts the song on top and plays it (does not wipe). `queue_track` (alias `queue_now`) queues up-next without skipping. Wipe is `replace_queue_with_track` only.

**1.0.0.76:** That keep-queue on-top play is the **default**. `play_library_track` description leads with Default play.

**Spotify lists (2026-10-11):** The Control tab stores Spotify links you paste, next to Sonos favorites. Each link has a name you can edit. **Remember** saves it and does not change playback. **Play** saves it, then switches the group onto that item. Saved rows live in `SavedSpotifyLinks` in settings. Account browse is not on this tab. Playback still uses the speaker's own Spotify account (`SA_RINCON3079`). A playlist or album is enqueued (`AddURIToQueue`) and the group queue is played; `SetAVTransportURI` of `x-rincon-cpcontainer:` returns UPnP **714**.

**Play tab (2026-10-11):** Folders, tags, and genres (when `ShowGenresInPlaySources` is on) are the **Play** tab, directly after Control. Play still shuffles that source. A folder stays in that folder for top-up. Sonos favorites and saved Spotify links stay on Control. That card sizes to its content. **Refresh** sits beside the Sonos heading. Sonos Radio tiles with no play URI are not listed.

**Daily speaker groups (2026-11-14):** Control → Shuffle, on the same line as **All speakers**. Check a saved group to use those rooms as the Daily group. Save the checked rooms with **Save group** inside Choose speakers. Right-click a group to remove it.

**Live grouping (2026-11-19):** The tray (Control) and the phone (Settings → Speakers & Daily group, and Rooms) show who is grouped from ZoneGroupTopology (`Grouped now: Office + Master Bath; 8 rooms ungrouped`, or `All speakers` only when one group holds every visible room). The Daily checkbox is labeled **Daily group preset** and is not that live picture. When they differ, the line is **Not grouped as Daily group, tap Regroup**. Regroup runs only when that control is used. A topology event or the 15s heartbeat refreshes the picture. The heartbeat does not regroup.

**Heard outside the library (2026-10-11):** When a named track plays from Spotify, radio, or another non-library source, HotSonos keeps the title, artist, album, source, and time. Library tracks are not listed. One row per track; a repeat updates last-heard and the play count (first-heard stays). The list is on the Control tab under Now playing (newest first, Copy, remove). File: `%LocalAppData%\HotSonos\heard-outside-library.json` (500 tracks, 90 days). MCP: `get_heard_tracks`. This is separate from shuffle `play-history.json` and the debug `play-events.jsonl`. The first launch imports non-library `started` rows from `play-events.jsonl`.

**Reports (activity log):** The Reports tab hosts a list of reports (add one by adding a UserControl and a `ReportCatalog` entry). Shipped reports: **Song Play History** (search a song; every play with zone, listened vs length, outcome, trigger; play count, skip count, last played), **Music** (chronological songs, date range, skipped-only, most played / most skipped / recently played), **Event Log** (Music vs Sonos/system). Each grid shows 500 rows at a time and **Load older** fetches the next page. Store: `%LocalAppData%\HotSonos\activity.db` (SQLite). Recording is on while the app runs. Shuffle exclusion is still only `play-history.json` — the activity log does not write that file and does not change who gets excluded. First open seeds sessions from `play-history.json` (one last-played row per track, outcome unknown) and from `play-events.jsonl` (started paired with skipped/stopped/previous; a terminal line with no start is skipped). The seed marker is written in the same transaction as the rows. Retention: 180 days, 80,000 events, 40,000 sessions. An open play is saved on exit. Outcome heuristic: see `PlayOutcomeClassifier` (HotSonos Next is always a skip; another controller's early track change is a skip; ~85% of any known duration, or the last 15s of a longer track, with no Next is completed). A failed Next, Previous, shuffle, or play-now does not keep that label. Speaker loss closes the play only when the playing coordinator drops. GENA NOTIFY is accepted only when SID and sender match the active subscription.

### Snapshot (2026-07-28)

| Item | State |
|------|--------|
| **Git HEAD** | See git log |
| **App version** | `1.0.0.70` |
| **MCP endpoint** | `http://127.0.0.1:42341/mcp` (tray app must be running; enabled by default) |
| **Main window** | Tabs: Control · Play · Hotkeys · Shuffle · Library · Tags · Wake · Options · Topology · Reports · Logs · Debug |
| **Play sources** | Control **From**: All / tag / genre · **Play** tab: folders, tags, genres · Control: Sonos favorites and **saved Spotify links** · **heard outside the library** (under Now playing) · Quick Play · slots 1–6 · MCP; genres optional (`ShowGenresInPlaySources`) |
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
- Shuffle history: GENA + **Next/skip** both mark tracks excluded (~14d). **ShuffleExcludePlayed** controls that history only. A top-up never re-adds a track that is still queued and unplayed. When every eligible track was heard in the last 24h, top-up replays already-heard tracks oldest first and still skips anything waiting; if nothing is eligible it appends nothing. A fresh shuffle (queue replace) is unchanged.  
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

**Phone remote** (§6.6): first launch with no saved pairing opens **Connect** (find the PC on UDP 42343, scan the QR on the PC Phone window, open or paste `hotsonos://pair`, or type the LAN address and PIN). Connecting uses **PC only** so every tab talks to the tray app. The phone opens on **Player**. Previous on Player and on the tray Control card is a left-pointing skip-previous icon, the mirror of next. **Quick tag** (the Quick tag hotkey, Ctrl+Alt+T) is a button on that Control card and on Player. The phone calls `GET` / `POST /api/now/tags` (same PIN as every other route) and toggles a catalog tag on the playing library track. **Settings** drills into Connection / PC (address, PIN, link mode), Playback & Shuffle, Spotify links, Library, Speakers & Daily group, Wake & nightly, and About. The address is not on the Player. LAN API stays on port **42342** with the 6-digit PIN. Client is `src/HotSonos.Remote` (.NET MAUI, not in `HotSonos.slnx`). Android is the default target and works as a remote or standalone. iOS (`net10.0-ios`, iPhone and iPad) is in early development and is added only when `HotSonosRemoteTargetFrameworks` is set or the build is on macOS, so Windows and Linux CI stay Android-only. Android and iOS both use `com.tangenttech.hotsonos`. TestFlight setup is `docs/ios.md`. The iOS project has not been compiled yet (no Mac on the change that added it). On Android, a media session and a foreground `mediaPlayback` notification mirror the 2-second now-playing poll (title, artist, album art, playing/paused). Transport and absolute volume 0–100 go to the PC client or the standalone engine, using the active room's group. Settings has **Show media controls (watch, lock screen)**, default on. Android 13+ is asked for `POST_NOTIFICATIONS`. No new hardware feature is required, so ChromeOS still installs the same package. A paired Pixel Watch's built-in media controls use that session. A Wear OS companion (`src/HotSonos.Wear`, same id `com.tangenttech.hotsonos`) shows title, artist, play/pause, skip, and volume, and talks to the phone over the Wearable Data Layer with no PIN (`docs/wear.md`). Library groups (§6.2) stay the next product slice.

---

## 1. Overview

| | |
|--|--|
| **Platform** | Windows 10/11 x64 only |
| **Runtime** | .NET 10+ (WPF + WinForms tray) |
| **Site / brand** | [hotsonos.com](https://hotsonos.com) — static landing + privacy page, source in `site/` (served from docker1 clover-dev nginx) |
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
| Play Spotify list | A saved name or a pasted share link. Remembered in settings with a name. Containers enqueue (`AddURIToQueue`), then the group queue plays. `SetAVTransportURI` of `x-rincon-cpcontainer:` returns UPnP **714**. |
| Heard outside the library | Control list under Now playing. Named Spotify, radio, and other non-library tracks. Copy the name. Library tracks are excluded. `%LocalAppData%\HotSonos\heard-outside-library.json`. MCP `get_heard_tracks`. |
| Shuffle Music Library | Browse `A:TRACKS`; **short queue (~80)**; hard-exclude tracks **played or skipped** (~14d) when that option is on; top-up never re-adds tracks still queued and unplayed, and when the unheard pool is empty replays already-heard tracks oldest-first (or appends nothing); **auto top-up** near queue end; artist spread; `NORMAL` |
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
| `src/HotSonos.Activity` | Play-session tracker, outcome heuristic, SQLite activity log for Reports |
| `src/HotSonos.App` | WPF tray app: hotkeys, settings, flyout, wake, nightly, Reports |
| `src/HotSonos.Harness` | Console harness against live speakers |
| `tests/HotSonos.Core.Tests` | Offline unit tests (parsers / playability) |
| `tests/HotSonos.Activity.Tests` | Play outcome, session tracker, activity database |

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
- Settings: `%LocalAppData%\HotSonos\settings.json` (`PhoneAdbHost`, `PhoneAdbPort` for wireless adb)
- Phone adb endpoint: `%LocalAppData%\HotSonos\phone-adb.txt` (`host:port`, rewritten whenever settings are saved)
- Play history (shuffle exclude only): `%LocalAppData%\HotSonos\play-history.json`  
- Activity / Reports: `%LocalAppData%\HotSonos\activity.db`  
- Logs: `%LocalAppData%\HotSonos\logs\hotsonos-yyyyMMdd.log` (7-day retention)  
- Version: `Directory.Build.props` (CI/release tags override)  
- MSI: per-user, self-contained win-x64, WiX  

---

## 4. Shipped features (v1.x)

### System tray
- Custom icon; version in tooltip/menu  
- Menu: Open, **Phone…**, refresh, fresh start, shuffle, transport, volume, level-all, rooms, favorites, offline line, diagnostics, exit  
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

**Daily group speakers (Control → Shuffle):** **Grouped now** is the live ZoneGroupTopology (refreshed on a topology event and on the 15-second heartbeat). **All speakers** on the next line is the **Daily group preset**, the rooms the next shuffle, Fresh start, and Regroup will join. Checking a saved group (`DailySpeakerPresets`) turns All off and copies that room list into the preset. It does not regroup by itself. When the preset and the live groups differ, the window says **Not grouped as Daily group, tap Regroup**. **Regroup** joins the preset and does not start a shuffle. **Restart fresh** regroups that selected list (not every speaker) and the line under the button names the group. The log line is `Fresh start regroup:`. **Choose speakers…** still edits the list by hand. **Save group** stores the checked rooms under the name in the box (same name replaces). Right-click a group to remove it. Unchecking the active group turns All speakers back on. The heartbeat pull does not schedule Keep house grouped.

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
- **KeepHouseGrouped** (default off): Topology tab checkbox **Keep house grouped**. When on, rooms that peel into their own groups while still online are pulled back under the coordinator after 8 seconds, at most once a minute. Off: no auto-regroup. **Regroup all** is the button for a split already on screen. The auto-regroup writes a flushed breadcrumb (`last-action.txt` and `lifecycle.log`) the moment GroupAll starts, and a "returned" line only if that call comes back. A tray death during the regroup leaves the start line with no returned line.




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
- Static site on **hotsonos.com** (download MSI, features, GitHub link) — live; source in `site/`. Home, privacy, and the 404 page open with the Sonos trademark notice from Settings → About (`AboutCopy.Trademark`); the footer keeps that same notice.  

### 6.5 Later (not now)
- Timed mode swap (play group A for N hours, then group B / Daily)  
- Multi-path group editor polish if v1 is single-folder only

### 6.6 Phone remote — full view of this PC (shipped)

The phone is a view of the tray app with the same control. PC mode does not discover speakers and does not send UPnP; that path is only **Speakers only**. MCP stays on `127.0.0.1:42341`. The phone listener stays on the LAN address that shares a /24 with a speaker.

**Why the PIN stayed.** HotList pairs with a long token on port 42356. HotSonos already ships a 6-digit house PIN on port **42342** and header `X-HotSonos-Pin`. Phones already save that PIN, and the listener still binds only the PC address that shares a /24 with a speaker (stricter than HotList's `0.0.0.0`). A new token format would break that pairing without changing who can open a socket on the house LAN. The PIN is still required on every route, compared in fixed time.

**PC pairing.** The main window has a top-bar **Phone** button (tray **Phone…** opens it too). It shows the listening address, the port, and the PIN in large type, **Copy**, **New PIN**, and the Private-network firewall command (TCP 42342 and UDP 42343). When the listener is bound it also shows a QR code for `hotsonos://pair?host=<ip>&port=42342&pin=<pin>` and **Copy link**. The address, port, and PIN text stay. Options still has the enable checkbox, port, and PIN. The phone cannot change the PIN, the port, or MCP.

**First launch.** With no saved pairing the phone opens **Connect**, not the Player tab and not speaker discovery. **Find PC** sends a UDP probe (`HS1?` to the local /24 broadcast and to 255.255.255.255, port **42343**). The tray answers with its LAN address and HTTP port only — never the PIN — and also announces that reply every couple of seconds on the speaker subnet. **Scan QR** reads the code on the PC Phone window. The same screen and **Settings → Connection / PC** accept a pasted `hotsonos://pair?host=<ip>&port=42342&pin=<pin>` link, and Android opens that link from an intent filter (`hotsonos` / `pair`). iOS registers the same scheme. On success the phone saves the host, port, and PIN and tests the PC (wrong PIN, timed out, unreachable, or any other failure stays as text on the page, and the fields can be tried again). **Test and connect** does the same for a typed address and PIN (blank port means 42342). It then stays in **PC only**. **Scan QR** is hidden when the device has no camera. `android.hardware.camera` stays `required=false`, including `camera.any`, so a Chromebook without a camera still installs. **Use speakers without the PC** is the secondary choice and is the only way to open the tabs with no pairing. An emulator does not share the PC's subnet, so Find PC will miss; scan the code, paste the link, or type the 192.168 address from the Phone window. `10.0.2.2` is the PC loopback, and the listener is not bound there.

The saved address, port, and PIN are the phone's private preferences for application id `com.tangenttech.hotsonos` (keys `pc.host`, `pc.port`, `pc.pin`). A normal update of that same id keeps them. Changing the id installs a different app and the saved pairing is gone. The previous id was `com.hotsonos.remote`.

**Player.** The first tab, and the screen the app opens on. Large album art, then title, artist, and album. That text is a tight block directly under the art: the art keeps its square size (it is not shrunk), and the title does not overlap the art. The page scrolls if the window is shorter than the art plus the controls. Previous, Play/Pause, and Next are large icon buttons (the middle button is Play or Pause from the transport state). Previous is a left-pointing skip-previous mark, the mirror of next. The tray Control now-playing card uses that same pair. Under that: a large **−** to the left of the volume number, a large **+** to the right, and a 40dp mute icon on that same line. The line is always `Volume 20%`, or `Volume 20% · muted` — never the word Volume alone. `GET /api/status` leaves house volume empty until the tray has set or sampled it, so the Player samples (`POST /api/volume/sample` on the PC, or a direct speaker read) and shows that percent. Later polls, − / +, dragging the slider, and the hardware volume keys all update the same line. Those buttons are the primary volume control. Each tap steps the house volume by the same amount as the phone's hardware volume keys: the PC's configured volume step while the phone is driving the PC, and 2 while it is driving the speakers. The result is clamped to 0–100 and the number changes as soon as the button is tapped. Holding a button repeats. The slider stays under that row and still sets an absolute level. **Mute** is the 40dp round button on the − / percent / + line: a speaker with sound waves when the group is open, and a slashed speaker on red when it is muted. **Level all** stays a text button under the slider. Steps and the slider do not unmute. **Quick tag** sits under Level all. It is the Quick tag hotkey (tag the playing library track) as a button. On the PC it loads `GET /api/now/tags` and toggles the chosen catalog tag with `POST /api/now/tags`. Both routes use the same PIN check as the rest of the listener. Standalone has no library write, so the button says Quick tag needs the PC. The screen still has no queue, no settings button, no favorite-slot list, no address, and no room checks. The same screen drives the PC and, in Speakers mode, the speakers directly.

**Sections.** Stacked lists on Now, Play, Rooms, Reports, and the Settings drill-ins (Playback, Library, Speakers, Wake, About) are headers with a chevron. Tap the header to hide or show the body. The phone remembers each choice. On a first visit the list you use most on that screen starts open (favorite slots, tags, house, groups, rooms, Daily group preset, library folders, shuffle, wake, report filters) and the rest start closed. Player stays fully open: now playing, transport, and volume are that whole tab, so they are not a section. Queue is one list. Settings itself is the menu of pages. Connection / PC and Spotify links are one form each.

**Mode.** Every tab except Player has a tappable line: `PC`, `Speakers`, `Speakers: <room>`, `PC unreachable`, or `PC: not paired`. A live PC does not show its address there. The line opens **Settings → Connection / PC**, which still shows the address, port, and PIN. The tab bar is the phone's own strip: **Player, Now, Play, Queue, Library, Rooms, Reports, Settings**. It scrolls sideways so the later labels stay tappable. Android's built-in bar only keeps five and was greying the rest out. A control that needs the PC says so and offers **Open connection** instead of sitting there disabled.

**What the phone can do.** Same delegates and action gate as the tray and MCP:

- **Player:** transport (previous is the left-pointing mirror of next), house volume (− / + by the hardware-key volume step, slider still sets an absolute level, logical level, room offsets, neither unmute), a 40dp mute icon on that volume line, level-all, and **Quick tag** for the playing library track (PC only; `GET` / `POST /api/now/tags`). PC mode and Speakers mode share transport and volume. Quick tag needs the PC
- **Now:** favorite slots and Sonos favorites. Tapping one starts playback
- **Play:** tags, genres, folders
- **Queue:** the queue list. On the speakers, tap a row to jump there. Each Library song row has Play and Wipe queue. There is no per-row Queue button
- **Settings → Playback & Shuffle:** shuffle source, Start shuffle, Daily shuffle, Fresh start, Resume shuffle, speaker-library sample, and the playback options (genres, GENA, shuffle sizes, level percent)
- **Settings → Speakers & Daily group:** **Grouped now** is the live topology from `GET /api/status` (`liveGrouping`). **Daily group preset** is the saved setting (`dailyGroupPreset`), not who is grouped. When `groupedAsDaily` is false the screen says **Not grouped as Daily group, tap Regroup**; **Regroup** asks first and joins the preset. The per-speaker check list (and Save group, which edits that list) stays hidden while **All speakers** is on. The switch and the All speakers line edit the preset. Turning All speakers off shows the room checks. The active room picker is on this page
- **Settings → Spotify links:** play, save, remove. Linking the Spotify account stays on the PC (it opens a browser there)
- **Settings → Library:** folder list (add a UNC path, Daily checkboxes, Discover from Sonos, one shared master folder), create / rename / delete tags, rescan, write-tags-to-master. The Library tab still searches and can toggle a tag on a track
- **Settings → Wake & nightly:** wake now / cancel and the nightly re-sync
- **Settings → About:** HotSonos, the version from AppInfo, app id `com.tangenttech.hotsonos`, links to https://hotsonos.com and https://github.com/nhustak/HotSonos, a short line that this build is a remote or standalone, and the Sonos trademark notice. Below that: heard-outside-the-library list, recent log lines, failure diagnostic
- **Rooms:** live ZoneGroupTopology. Each card is one real group or one room on its own, with the coordinator, play state (Playing, Paused, Stopped), and bonded units such as a Sub folded under that room. **Grouped now**, the Daily group preset, and the drift line stay. When `groupedAsDaily` is false the line says **Not grouped as Daily group, tap Regroup**; **Regroup** asks first and joins the preset. The confirm dialog is shown by the shell page. The tab page itself is not attached to a window (its body is lifted into the shell), and a dialog on that page never appears, so the button used to return before `POST /api/topology/regroup`. PC mode sends that post. Speakers mode joins every other room to the active coordinator (`SetCoordinatorAsync` with the whole house). A press dims the control and ticks; Regroup, Reboot, Level all, Quick tag, and transport stay disabled until a short toast (`Regrouped`, or the error text). Remote errors are shown that way instead of failing quietly. `GET /api/topology` pulls `GetZoneGroupState` before it answers, so pull-down, **Refresh**, and a change in the grouping line or the playback state are a new snapshot plus each coordinator's transport, not only the last cached picture. The handler returns that JSON; an empty 200 is a bug. If the call fails, Rooms still clears the sections, shows **No topology yet**, and loads each room from `GET /api/volumes`. Each room is one compact row: the room name, what's playing when this phone knows it (the current track on rooms in the active group; Playing or Paused on another group that is not stopped), and on that same line **−**, the percent, **+**, and the 40dp mute icon (`VolumeLine`). There is no per-room slider. **−** and **+** step by the same amount as the Player volume buttons. Make coordinator and Reboot are not buttons on the row. A long-press on the room (a right-click on a Chromebook) opens that sheet. Speakers mode offers Leave group instead of Reboot. Keep house grouped, Refresh, Regroup, and Reboot all stay on the House section. Grouping still works
- **Reports:** Song Play History, Music, Event Log (`activity.db`)

Reboot, wipe queue, rescan, regroup, fresh start, and tag delete ask for confirmation on the phone before the request is sent. That dialog, and every other alert or action sheet from a tab, is hosted by the shell page.

**Live state.** While the shell is open the phone polls `GET /api/status` every 2 seconds. That route reads memory (now playing, house volume, groups, wake, busy, `liveGrouping`, `dailyGroupPreset`, `groupedAsDaily`). The tray refreshes that memory on a ZoneGroupTopology event and on the 15-second heartbeat (one `GetZoneGroupState` to a known speaker, no regroup). Album art is fetched only when its revision changes. Sources, topology, reports, and settings load when that tab appears and on refresh. A push socket would be a second long-lived connection for the same small JSON, and it dies when the phone sleeps. Two seconds is prompt enough for a remote and matches HotList's pull-on-open model plus a timer, because playback changes when nobody touches the phone.

**Media controls (Android).** While the shell is open and **Show media controls (watch, lock screen)** is on (Settings, default on), the phone publishes an Android `MediaSession` (`MediaSessionCompat`) plus a foreground notification with `foregroundServiceType="mediaPlayback"`. The notification shows the polled title, artist, and album art, and the session state is playing or paused (a stopped transport is published as paused so the watch keeps a play button). Play, pause, next, and previous call the same PC or standalone transport as the Player buttons. Volume is `VolumeProviderCompat` with `VOLUME_CONTROL_ABSOLUTE` and max 100, mapped to the active room's group: PC mode writes the tray's house volume, standalone writes each player in the active coordinator's group. Android 13+ asks for `POST_NOTIFICATIONS` once. The media notification is a foreground-service notification, so the session still starts if that prompt is denied. Turning the switch off stops the service and removes the notification. The manifest adds no required hardware feature.

**Watch companion.** `src/HotSonos.Wear` is a Kotlin Compose for Wear OS app (MAUI has no Wear target). Application id `com.tangenttech.hotsonos`, the same id as the phone, so the Wearable Data Layer can deliver `MessageClient` traffic on `/hotsonos/v1`. The watch sends `play`, `pause`, `next`, `previous`, `volume-up`, `volume-down`, and `refresh`. The phone answers with JSON `{title, artist, playing, volume, nextTitle, nextArtist}`. `nextTitle` and `nextArtist` are the queue row after the playing track (position + 1), in PC mode and on the speakers. Next on the watch paints that row immediately and keeps it until a snapshot names a different title (a snapshot that still names the track just left is ignored for 4 seconds). After play, pause, next, or previous the phone reads status once and pushes that snapshot, instead of waiting for the 2-second poll. The phone listener forwards those commands to the PC client or the standalone engine and does not ask the watch for a PIN or a network address. Volume steps use the phone's speaker step (2) against a local target so a fast crown does not repeat one stale poll; the target is dropped when a later poll matches it or after 3 seconds. Debug phone builds and the watch APK share `src/HotSonos.Wear/debug.keystore`. The companion stays available when **Show media controls** is off. The listener adds no required hardware feature. Install steps are `docs/wear.md`.

**Phone app.** `src/HotSonos.Remote` is a .NET MAUI app. Android is `net10.0-android`, minSdk 24. iOS is `net10.0-ios` (iOS 15 or later, iPhone and iPad) when the build passes `-p:HotSonosRemoteTargetFrameworks=net10.0-ios`, or on macOS without that property. Windows and Linux, including GitHub Actions, do not pass it and are not macOS, so they stay on `-f net10.0-android`. The property is read only by the remote project. `HotSonos.Remote.Protocol` and `HotSonos.Core` stay `net10.0`. Android package and iOS bundle id: `com.tangenttech.hotsonos`. Display name: HotSonos. Tabs: Player, Now, Play, Queue, Library, Rooms, Reports, Settings. It is not in `HotSonos.slnx` and not in the MSI, so the Windows build does not need a mobile workload. Shared DTOs: `src/HotSonos.Remote.Protocol`. The speaker client is `HotSonos.Core` (already `net10.0`, no Windows APIs): SSDP, SOAP, transport, favorites, and the speaker Music Library. Volume and grouping for the phone live in `SonosDirect` in that same library. Release publish on Android signs with `%USERPROFILE%\.android-keys\hotsonos\` when that upload key exists, and with the machine debug key otherwise (`HOTSONOS_UPLOAD_KEY_DIR` overrides the folder). Debug phone builds sign with `src/HotSonos.Wear/debug.keystore` so the Wear OS companion, which uses that same certificate, can use the Data Layer.

**iOS and iPad.** `Info.plist` sets `UIDeviceFamily` to 1 and 2, iPad orientations (portrait, upside down, landscape left, landscape right), `NSLocalNetworkUsageDescription`, and `NSAppTransportSecurity` / `NSAllowsLocalNetworking` so plain HTTP to the PC and the speakers is allowed. There is no `NSBonjourServices` key: discovery is not mDNS. Find PC is UDP port 42343. Speaker search is SSDP. Both need `com.apple.developer.networking.multicast`, which Apple approves separately. `Entitlements.plist` does not contain that key, so a miss leaves the address fields usable and says to scan the code, paste the pairing link, or type the PC address. `NSCameraUsageDescription` is set for Scan QR. `CFBundleURLTypes` registers the `hotsonos` scheme so a pairing link opens the app. On iPad the Player column is centered and capped at 520 points. The tab strip centers when the eight labels fit. iPhone layout is unchanged. Android phone width is unchanged; a wide Chromebook window is under ChromeOS. TestFlight is `codemagic.yaml`: `ios-build-check` publishes an unsigned `iossimulator-arm64` build on pull requests, and `ios-testflight` publishes a signed `ios-arm64` IPA to TestFlight on `master`. The steps to enroll, create the App ID and the App Store Connect record, and fill the placeholders are `docs/ios.md`. The iOS target has not been compiled on a Mac yet.

**ChromeOS.** The same Android package runs on a Chromebook. The activity is resizable, is not locked to one orientation, and keeps its state when the window changes size. Touchscreen, camera, telephony, and the other hardware a clamshell may lack are not required. **Scan QR** is hidden when the Chromebook has no camera; paste the pairing link or type the address. The camera feature stays optional, so the package still installs. On a phone-width window the Player is unchanged; a wider window centers that column (album art stays square, the − / + and Level all buttons stay phone-sized, and the mute icon stays 40dp on the volume line) and the bottom tabs stay, spreading once they fit. Space plays or pauses, Left/Right or media previous/next skip, Up/Down or +/- use the same volume step as the hardware keys, M mutes, and media keys work. Those keys stay out of a text field. ChromeOS NATs the Android container, so UDP broadcast on 42343 often cannot see the PC; Find PC then says to scan the code, paste the pairing link, or type the address under Settings → Connection / PC. Sideload from Play internal testing, or with adb while ChromeOS ADB debugging is on (README). The media-controls session is the same Android notification. It does not require a watch, Bluetooth, or any other hardware, and the Settings switch turns it off. The Wear OS companion is a separate package (`src/HotSonos.Wear`, `docs/wear.md`) and is not installed on the Chromebook. The phone's message listener does not require watch hardware.

**Standalone.** The phone can run with no PC after **Use speakers without the PC**. Link (Settings → Connection / PC): **Auto** tries the paired PC for 2 seconds and uses the speakers when it does not answer; **PC only** (what Connect saves) never falls back and keeps polling the PC; **Speakers only** never calls the PC. The choice is rechecked when the app resumes. The line at the top of each screen except Player says `PC`, `Speakers`, `Speakers: room`, `PC unreachable`, or `PC: not paired`.

Standalone uses SSDP M-SEARCH for ZonePlayer (`239.255.255.250:1900`) while holding a WifiManager multicast lock (`CHANGE_WIFI_MULTICAST_STATE`), then the same SOAP services as the tray app: ZoneGroupTopology, AVTransport, RenderingControl, ContentDirectory. It does now playing (title, artist, album, art), play/pause/next/previous/seek, house and per-room volume and mute, group/ungroup and set coordinator, Sonos favorites and Sonos playlists, a short queue list, search/browse of the speaker's indexed Music Library (`A:`), play/queue from that, and a random sample from it. That sample is not HotSonos shuffle: no last-played history and no top-up.

HotSonos shuffle, tags, Daily speaker presets, Reports, wake/nightly, and saved Spotify links stay labeled **needs the PC** in speaker mode. PC mode, the PIN, and the LAN-only bind are unchanged. MCP stays on `127.0.0.1:42341`.

**Not on the phone.** Hotkey capture, the Quick tag and Quick play overlays, tray double-click, flyout, and Start with Windows are PC shell (they need the Windows session). The playing-track Quick tag action itself is on Player. Per-folder master maps that differ from each other stay on the PC; the phone can set one shared master path as text. There is no file-browse dialog. Bass/treble EQ is not on this surface. Playlist-from-filter is not shipped on the desktop either. Deleting audio files from disk stays on the PC. Spotify account linking still opens a browser on the PC. The phone cannot change the PIN, the port, or MCP.

**Wireless debugging (PC only).** The Phone window has a **Phone wireless debugging** section for deploying the Android app over Wi-Fi. It edits the phone adb IP (default `192.168.1.171`) and the connect port (default `34011`, which changes when the phone reboots). **Pair** runs `adb pair host:pairPort` with the 6-digit code from the phone. **Connect** runs `adb connect host:port`. Both save the IP and connect port first. Pairing port and pairing code are typed when the phone shows them and are not settings. The code is one-time: it is not written to `settings.json`, `phone-adb.txt`, or the log, and the command line shown in the window redacts it. Settings properties: `PhoneAdbHost` and `PhoneAdbPort`. The same save writes `host:port` to `%LocalAppData%\HotSonos\phone-adb.txt` so deploy scripts can read it. adb is `%LocalAppData%\Android\Sdk\platform-tools\adb.exe`, or `adb` on PATH. This does not change the remote PIN, the listener on 42342, hotkeys, or MCP on `127.0.0.1:42341`.

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
| Phone remote | Full-control LAN client of the tray app (port 42342, 6-digit PIN, .NET MAUI, id `com.tangenttech.hotsonos`) and a standalone speaker client (Auto / PC only / Speakers only). Android and ChromeOS work that way. iPhone and iPad are in early development (Codemagic / TestFlight). Pairing is the QR / `hotsonos://pair` link on the PC Phone window, or a typed address and PIN. The saved pairing survives an update of that app id and is wiped if the id changes. Previous is a left-pointing skip icon, the mirror of next. Quick tag for the playing track is a button on Player and the tray Control card (`GET` / `POST /api/now/tags`, same PIN). MCP stays loopback. Camera is optional. |
| Phone wireless debugging | Phone window: adb IP + connect port (`PhoneAdbHost`, `PhoneAdbPort`, `phone-adb.txt`). Pairing code is one-time and is not stored. |
| Phone media controls | Android `MediaSessionCompat` and a `mediaPlayback` foreground notification follow the 2-second poll. Play, pause, skip, and absolute volume 0–100 use the PC client or the standalone group. Settings **Show media controls (watch, lock screen)** defaults on. No required hardware feature, so ChromeOS is unchanged. |
| Wear OS companion | Kotlin app `src/HotSonos.Wear`, same application id as the phone. `MessageClient` on `/hotsonos/v1` so the watch has no PIN. Debug builds share `debug.keystore`. The media-controls switch does not disable it. |
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
| 2026-10-04 | §6.6 phone remote: LAN API on the tray app, Android client is a remote only |
| 2026-11-18 | §6.6 phone is a full view: MAUI tabs, Phone pairing screen, restrictions removed. PIN and port 42342 kept |
| 2026-11-18 | §6.6 phone also runs standalone on the speakers (Auto / PC only / Speakers only). PIN and port 42342 unchanged |
| 2026-11-19 | §6.6 phone Now: hide the Daily per-speaker check list while All speakers is on |
| 2026-11-19 | Tray and phone show live ZoneGroupTopology separately from the Daily group preset. No auto-regroup |
| 2026-11-20 | §6.6 phone: Player is the landing tab. Now keeps favorite slots and Sonos favorites. Queue and Settings are tabs. The mode line no longer shows the PC address. Grouped now and Regroup are on Settings → Speakers & Daily group and on Rooms |
| 2026-11-20 | §6.6 phone Player: − and + step house volume by the hardware-key volume step. The number updates immediately. Slider, Mute, and Level all stay |
| 2026-11-20 | §6.6 phone Player volume line always includes the percent. Status often has no house volume until a sample, so the Player samples and then follows polls, − / +, the slider, and the hardware keys |
| 2026-11-20 | §6.6 Phone window: wireless adb IP, connect port, optional pair. Code is not saved |
| 2026-11-20 | §6.6 phone: stacked lists collapse behind a header and chevron. The phone remembers open or closed. Player stays fully open |
| 2026-11-20 | §6.6 phone Rooms: live topology cards with coordinator, folded Sub, play state, and Regroup when that differs from the Daily group |
| 2026-11-20 | §6.6 iPhone and iPad: `net10.0-ios` behind `HotSonosRemoteTargetFrameworks` so Windows and Linux CI stay Android-only. iPad Player column centered at 520. Codemagic TestFlight (`codemagic.yaml`, `docs/ios.md`). Multicast entitlement documented and not embedded. iOS compile not yet run |
| 2026-11-20 | Phone id on Android and iOS is `com.tangenttech.hotsonos`. The previous id was `com.hotsonos.remote`. A sideload of the new package does not replace the old one |
| 2026-11-22 | §6.6 `GET /api/topology` writes its JSON. A failed call still clears Rooms, shows No topology yet, and loads speakers and per-room volume from `GET /api/volumes` |
| 2026-11-22 | §6.6 phone mute is a 40dp speaker icon on the volume line (Player − / percent / +, and each Rooms volume row). Per-room volume is − / percent / +, not a slider. The Player slider and Level all stay |
| 2026-11-22 | Previous on Player and the tray Control card is a left-pointing skip-previous icon, the mirror of next. Quick tag (the playing-track hotkey) is a button on both. Phone uses `GET` / `POST /api/now/tags` behind the existing PIN |
| 2026-11-22 | §6.6 pairing: PC Phone window QR for `hotsonos://pair`. Phone scans it, opens the link, or pastes it, then saves and tests. Camera stays optional. Same app id keeps the saved pairing; an id change wipes it |
| 2026-11-22 | §6.6 phone: Library song rows drop the Queue button (Play and Wipe queue stay). Rooms rows are the name, what's playing, and the 40dp volume line; coordinator and reboot move to a long-press. About leads with name, AppInfo version, app id, site, GitHub, and the Sonos trademark. Player title sits under the art and does not overlap it; art size and the slider stay |
| 2026-11-22 | §6.6 Android media session for a paired watch and the lock screen. Settings **Show media controls (watch, lock screen)**, default on. `POST_NOTIFICATIONS` and `mediaPlayback` foreground service. ChromeOS feature set unchanged |
| 2026-11-22 | §6.6 Wear OS companion `src/HotSonos.Wear`. Same application id. Data Layer messages, no PIN. Shared debug keystore. `docs/wear.md` |
| 2026-11-22 | §6.6 phone Regroup: confirms run on the shell page, so Rooms Regroup reaches `POST /api/topology/regroup` (speakers mode joins the house). Buttons tick and dim; long actions toast the result or the error. Every phone action except the status poll and art is one tray log line. The watch snapshot includes the next queue track and Next shows it immediately; transport pushes a fresh snapshot |
| 2026-11-22 | Shuffle top-up never re-appends a track still waiting on the queue. The empty unheard pool replays already-heard tracks oldest first and skips anything still waiting; it appends nothing when none are eligible. ShuffleExcludePlayed only controls recently heard tracks. A fresh shuffle is unchanged |
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
| 2026-10-11 | Spotify lists: Control columns (folders, tags, genres, Sonos, Spotify). Pasted links are saved with a name. Playback stays on the speaker. |
| 2026-10-11 | Heard outside the library: Control list of non-library tracks (title, artist, source, time) so a name can be copied later. |
| 2026-10-11 | Play tab (after Control): folders, tags, and genres. Sonos favorites and saved Spotify links stay on Control. |
