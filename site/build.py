#!/usr/bin/env python3
"""Builds the static HotSonos site. Edit the page bodies below, then run: python3 build.py
Logo lives in ONE file: assets/logo.png. To swap it, replace assets/logo.png and run make-icons.py."""
import pathlib
SITE="https://hotsonos.com"
REL="https://github.com/nhustak/HotSonos/releases/latest"
REPO="https://github.com/nhustak/HotSonos"
def head(title,desc,path):
    return f'''<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>{title}</title>
<meta name="description" content="{desc}">
<link rel="canonical" href="{SITE}{path}">
<meta name="theme-color" content="#16181d">
<link rel="icon" href="/favicon.ico" sizes="any">
<link rel="icon" type="image/png" sizes="32x32" href="/favicon-32.png">
<link rel="icon" type="image/png" sizes="16x16" href="/favicon-16.png">
<link rel="apple-touch-icon" href="/apple-touch-icon.png">
<link rel="manifest" href="/site.webmanifest">
<meta property="og:type" content="website">
<meta property="og:site_name" content="HotSonos">
<meta property="og:title" content="{title}">
<meta property="og:description" content="{desc}">
<meta property="og:url" content="{SITE}{path}">
<meta property="og:image" content="{SITE}/assets/og-image.png">
<meta property="og:image:width" content="1200">
<meta property="og:image:height" content="630">
<meta name="twitter:card" content="summary_large_image">
<meta name="twitter:title" content="{title}">
<meta name="twitter:description" content="{desc}">
<meta name="twitter:image" content="{SITE}/assets/og-image.png">
<link rel="stylesheet" href="/assets/site.css">
</head>
<body>
<header class="top"><div class="wrap">
<a class="brand" href="/"><img src="/assets/logo.png" alt="" width="34" height="34">HotSonos</a>
<nav><a class="hide-sm" href="/#features">Features</a><a class="hide-sm" href="/#faq">FAQ</a><a href="/#download">Download</a><a href="/privacy.html">Privacy</a></nav>
</div></header>
'''
FOOT=f'''<footer><div class="wrap">
<div class="row"><a class="brand" href="/"><img src="/assets/logo.png" alt="" width="28" height="28">HotSonos</a>
<div><a href="{REPO}">GitHub</a> · <a href="{REL}">Releases</a> · <a href="/privacy.html">Privacy</a> · <a href="{REPO}/blob/master/LICENSE">MIT License</a></div></div>
<p>Open source, maintained by <a href="https://github.com/nhustak">Nick Hustak</a>. © 2026 Nick Hustak.</p>
<p class="legal">HotSonos is an independent project and is not affiliated with or endorsed by Sonos, Inc. Sonos is a trademark of Sonos, Inc.</p>
</div></footer>
</body>
</html>
'''
DESC="HotSonos is a free Windows tray app that controls your Sonos speakers with global hotkeys: history-aware shuffle, whole-house volume, grouping, now playing, and play reports. Local network only."
INDEX=head("HotSonos — global hotkeys for your Sonos system",DESC,"/")+f'''
<main>
<section class="hero" style="border-top:0"><div class="wrap">
<div>
<span class="eyebrow">Free · Open source · Windows 10/11</span>
<h1>Your whole house of Sonos, <span class="g">one hotkey away.</span></h1>
<p class="lead">HotSonos lives in the Windows system tray and gives you instant, global keyboard shortcuts for shuffle, play/pause, skip, and whole-house volume — talking to your speakers directly over your local network. No cloud, no Sonos account.</p>
<div class="ctas"><a class="btn btn-primary" href="{REL}">Download for Windows</a><a class="btn btn-ghost" href="{REPO}">View on GitHub</a></div>
<p class="small" style="margin-top:14px">Per-user MSI installer, .NET runtime included.</p>
</div>
<img class="hero-logo" src="/assets/logo.png" alt="HotSonos logo" width="320" height="320">
</div></section>

<section id="features"><div class="wrap">
<h2>Everything the Sonos app makes you click through</h2>
<p class="sub">Built for people who listen all day and want music to just start — the right speakers, the right volume, nothing you heard this morning.</p>
<div class="grid">
<div class="card"><div class="ico">🔀</div><h3>History-aware shuffle</h3><p>Groups your speakers and builds a short random mix from your library, leaving out songs you've heard recently. When it runs low, a fresh batch is added automatically.</p></div>
<div class="card"><div class="ico">⌨️</div><h3>Global hotkeys</h3><p>Shuffle, play/pause, next, previous, volume up/down, and mute from any app. Every shortcut is re-bindable.</p></div>
<div class="card"><div class="ico">🔉</div><h3>Whole-house volume</h3><p>One "house" volume level for every room, with per-room offsets for amp-fed speakers, plus a one-click Level all.</p></div>
<div class="card"><div class="ico">🔗</div><h3>Grouping &amp; Daily groups</h3><p>Shuffle to all speakers or a checked subset of rooms, save named Daily speaker groups, and pick which room leads the group.</p></div>
<div class="card"><div class="ico">🎵</div><h3>Now-playing flyout</h3><p>Album art, title, artist, and state in a draggable, pinnable flyout that updates live from your speakers.</p></div>
<div class="card"><div class="ico">🏷️</div><h3>Tags, genres &amp; folders</h3><p>Tag tracks into moods like Dinner or Focus, then play a tag, genre, or library folder as its own shuffle. Quick Tag and Quick Play overlays keep it keyboard-first.</p></div>
<div class="card"><div class="ico">📊</div><h3>Reports</h3><p>Song play history, most played and most skipped songs, completed vs. skipped outcomes, and an event log of music and speaker activity.</p></div>
<div class="card"><div class="ico">☀️</div><h3>Wake to music</h3><p>Schedule music to start in a room with a gentle volume ramp, from a favorite or a shuffle, optionally expanding to the whole house.</p></div>
<div class="card"><div class="ico">🤖</div><h3>Local MCP server</h3><p>An optional loopback MCP endpoint on this PC lets AI assistants search your library and control playback.</p></div>
</div>
<p class="small" style="margin-top:22px">Also: favorite slots with their own hotkeys, Restart fresh (re-sync speakers and reshuffle), live speaker monitoring with auto-rejoin, and a phone remote for Android that's in progress.</p>
</div></section>

<section class="alt" id="screens"><div class="wrap">
<h2>See it in action</h2>
<p class="sub">Screenshots are on the way.</p>
<div class="shot"><div><strong>Screenshot coming soon</strong><br><span class="small">Tray flyout · Control tab · Reports</span></div></div>
</div></section>

<section id="hotkeys"><div class="wrap two">
<div><h2>Default hotkeys</h2><p class="sub">Change any of them under Hotkeys in the tray app.</p></div>
<table class="keys">
<tr><td>Shuffle music library to all speakers</td><td><kbd>Ctrl</kbd> <kbd>Alt</kbd> <kbd>F8</kbd></td></tr>
<tr><td>Play / Pause</td><td><kbd>Ctrl</kbd> <kbd>Alt</kbd> <kbd>F9</kbd></td></tr>
<tr><td>Previous / Next track</td><td><kbd>Ctrl</kbd> <kbd>Alt</kbd> <kbd>F10</kbd> / <kbd>F11</kbd></td></tr>
<tr><td>Volume up / down</td><td><kbd>Ctrl</kbd> <kbd>Alt</kbd> <kbd>↑</kbd> / <kbd>↓</kbd></td></tr>
<tr><td>Mute / Unmute</td><td><kbd>Ctrl</kbd> <kbd>Alt</kbd> <kbd>M</kbd></td></tr>
<tr><td>Quick Tag / Quick Play</td><td><kbd>Ctrl</kbd> <kbd>Alt</kbd> <kbd>T</kbd> / <kbd>P</kbd></td></tr>
</table>
</div></section>

<section class="alt" id="download"><div class="wrap">
<h2>Download</h2>
<p class="sub">Free and open source under the MIT license.</p>
<div class="dl">
<img src="/assets/logo.png" alt="" width="72" height="72">
<div style="flex:1;min-width:240px"><strong style="font-size:1.15rem">HotSonos for Windows</strong><br><span class="small">Grab <code>HotSonos-x.y.z.msi</code> from the latest GitHub release. Installs per user (no admin) and includes the .NET runtime.</span></div>
<a class="btn btn-primary" href="{REL}">Latest release</a>
</div>
<p class="small" style="margin-top:14px">The installer isn't code-signed yet, so Windows SmartScreen may ask you to choose <em>More info → Run anyway</em>.</p>
</div></section>

<section id="requirements"><div class="wrap two">
<div><h2>Requirements</h2><p class="sub">That's it — no account, no subscription.</p></div>
<ul class="check">
<li>Windows 10 or Windows 11</li>
<li>Sonos speakers (S1 or S2) on the same local network as the PC</li>
<li>For library shuffle and tagging: network (SMB) access from the PC to the music share your Sonos system indexes</li>
<li>For wake-up music and nightly re-sync: the PC awake with HotSonos running</li>
</ul>
</div></section>

<section class="alt" id="faq"><div class="wrap">
<h2>FAQ</h2>
<details><summary>Does HotSonos need a Sonos account or the internet?</summary><p>No. It finds and controls your speakers directly on your local network, the same way the speakers talk to each other. There's no HotSonos account and no HotSonos server.</p></details>
<details><summary>Does it replace the Sonos app?</summary><p>No — it sits alongside it. Use the Sonos app for setup and music services; use HotSonos for fast, keyboard-first everyday control.</p></details>
<details><summary>How does the shuffle avoid repeats?</summary><p>As songs play or you skip them, HotSonos remembers them and leaves them out of the next batch (by default, roughly the last couple of weeks). If you've heard most of your library, it loosens that rule so music keeps playing.</p></details>
<details><summary>Is there a Mac or phone version?</summary><p>HotSonos is Windows-only. An Android remote that controls the HotSonos app on your PC over your home network is in progress.</p></details>
<details><summary>My speakers stutter or drop out of groups. Can HotSonos help?</summary><p>Restart fresh re-discovers and regroups speakers and starts a new shuffle. If problems persist, the README explains why turning off SonosNet and choosing a 5 GHz group coordinator often helps.</p></details>
<details><summary>Why does Windows warn me when I install it?</summary><p>The MSI isn't code-signed yet. SmartScreen may show a prompt; choose More info → Run anyway.</p></details>
<details><summary>Is it free?</summary><p>Yes. HotSonos is open source under the MIT license. The source and every release are on <a href="{REPO}">GitHub</a>.</p></details>
</div></section>
</main>
'''+FOOT

PRIV=head("Privacy — HotSonos","How HotSonos handles your data: it runs on your PC, stores data locally, and talks to your speakers on your local network.","/privacy.html")+f'''
<main class="doc">
<h1>Privacy</h1>
<p class="small">Last updated November 18, 2026</p>
<p>HotSonos is a Windows tray app that runs on your own PC. It has no HotSonos account, no HotSonos server, no analytics, no ads, and no telemetry. The developer never receives your data.</p>

<h2>What stays on your PC</h2>
<p>HotSonos keeps its data in <code>%LocalAppData%\\HotSonos</code> on your computer, including:</p>
<ul>
<li>Settings (<code>settings.json</code>), including hotkeys, speaker groups, and saved Spotify links you paste in</li>
<li>Shuffle play history (<code>play-history.json</code>) so recent songs aren't repeated</li>
<li>Reports and activity log (<code>activity.db</code>) — song plays, skips, and speaker events, kept up to 180 days</li>
<li>A cache of your music library's metadata (<code>library.db</code>) and tracks heard outside your library</li>
<li>Log files (<code>logs</code>)</li>
</ul>
<p>Uninstalling HotSonos and deleting that folder removes all of it.</p>

<h2>Who HotSonos talks to</h2>
<ul>
<li><strong>Your Sonos speakers, on your local network.</strong> HotSonos discovers and controls them directly over the LAN. Album art is loaded from the address your speaker reports for the current track.</li>
<li><strong>Your music share.</strong> To build the library cache and write tags into your music files, HotSonos reads (and, for tagging, writes) the network share your Sonos system already indexes.</li>
<li><strong>Your music services, only through your speakers.</strong> Music from Spotify, radio, and other services is played by your speakers using the accounts already linked in the Sonos app. HotSonos does not ask for those passwords.</li>
<li><strong>Optional Spotify playlist link (developer/debug tool).</strong> If you use HotSonos's debug tool to link Spotify for playlist browsing, HotSonos talks to the Spotify music-service address your Sonos speaker reports, and stores the resulting session token locally on your PC. This is off unless you start it.</li>
</ul>

<h2>Local MCP server (optional)</h2>
<p>When turned on, the MCP endpoint for AI assistants listens only on this PC's loopback address (<code>127.0.0.1</code>). Other devices on your network can't reach it. Any AI assistant you connect to it is governed by that assistant's own privacy policy.</p>

<h2>Phone remote (Android, in progress)</h2>
<p>The Android remote is a remote control for the HotSonos app on your PC; it does not talk to your speakers or to the internet. It connects to your PC's local network address and sends a 6-digit house PIN, shown in the PC tray app, with every request. The PC rejects requests without the correct PIN. The phone stores only the PC address and PIN in the app's private storage. The remote listener on the PC can be turned off in Options, and the firewall rule HotSonos offers to add applies to Private networks only.</p>

<h2>This website</h2>
<p>hotsonos.com is a static site with no cookies, trackers, or analytics. Like any web server, it may keep standard access logs (such as IP address and pages requested) for security and troubleshooting. Download links go to GitHub, which has its own privacy policy.</p>

<h2>Contact</h2>
<p>Questions? Open an issue on <a href="{REPO}/issues">GitHub</a>.</p>
</main>
'''+FOOT

NF=head("Page not found — HotSonos","That page doesn't exist.","/404.html")+'''
<main class="doc" style="text-align:center">
<img src="/assets/logo.png" alt="" width="120" height="120" style="margin:0 auto 18px">
<h1>Page not found</h1>
<p>That page went quiet. Let's get the music back on.</p>
<p><a class="btn btn-primary" href="/">Back to HotSonos</a></p>
</main>
'''+FOOT
out=pathlib.Path(__file__).parent
(out/"index.html").write_text(INDEX,encoding="utf-8")
(out/"privacy.html").write_text(PRIV,encoding="utf-8")
(out/"404.html").write_text(NF,encoding="utf-8")
(out/"robots.txt").write_text(f"User-agent: *\nAllow: /\n\nSitemap: {SITE}/sitemap.xml\n")
(out/"sitemap.xml").write_text(f'''<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
  <url><loc>{SITE}/</loc><lastmod>2026-11-18</lastmod></url>
  <url><loc>{SITE}/privacy.html</loc><lastmod>2026-11-18</lastmod></url>
</urlset>
''')
(out/"site.webmanifest").write_text('{"name":"HotSonos","short_name":"HotSonos","icons":[{"src":"/icon-192.png","sizes":"192x192","type":"image/png"},{"src":"/icon-512.png","sizes":"512x512","type":"image/png"}],"theme_color":"#16181d","background_color":"#16181d","display":"standalone"}\n')
print("built")
