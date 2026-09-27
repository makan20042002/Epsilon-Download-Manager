# Makan Download Manager 16.0.0

A fast, private download manager for Windows 10/11 (WPF, .NET 8) with Chrome, Edge, Brave and Firefox integration.
**Nothing ever starts by itself: you always choose Start Download or Download Later.**

## What it does
- **Fast, resumable downloads:** up to 16 connections into one pre-allocated file, exact-byte resume after pause, crash or restart, retries with back-off (`Retry-After` honoured), a **global Speed Limiter in the toolbar** (like IDM's) plus per-download limits, SHA-256 check when you give a checksum.
- **BitTorrent:** magnet links and `.torrent` files, DHT and peer exchange, HTTP/UDP trackers, per-file priority (Skip/Normal/High), resume that re-checks a torrent's files if they changed while Makan was closed, seeding with a share-ratio or time limit, and global + per-torrent download/upload limits. Double-click a torrent for its details window (files, peers, trackers). A magnet link clicked on a page is captured by the extension the same way a regular download is.
- **Smart connections:** Makan uses the number of connections you chose. A server that answers *too many requests* (429 / 503) gets fewer connections next time; this is remembered between runs and grows back after clean downloads. Turn it off in Options > Connection.
- **Browser integration:** the extension takes over downloads only after Makan has accepted the job (otherwise the browser keeps its own download), sends cookies and referrer for logged-in downloads, adds a *Download this video* button, a selected-links bar and *Download all links*, and a keyboard shortcut (**Alt+Shift+M**) that sends every link of the page.
- **Video:** HLS (`.m3u8`, AES-128, fMP4, separate audio) and DASH (`.mpd`) natively; **YouTube and many other protected sites through yt-dlp** (qualities, audio only, subtitles): Options > YouTube & other sites > *Download / update tools* sets it up once.
- **IDM-style windows:** main window with categories and columns, *Download File Info* dialog, **download progress window** (status, speed limiter, options on completion, per-connection view), Scheduler with queues ("download N files at the same time", start/stop times, shut down / exit when done), batch downloads with wildcards, site grabber, drop target.
- **Intelligent Center** (toolbar): what Makan learned about each server, one-click **bandwidth profiles** (Unlimited, Gaming, Work, Night), **health checks** (database, disk, network, browser link, tools), live statistics, crash-recovery list, a planner that previews folder and connections for an address, a download basket, and database check / backup.
- **Private by design:** browser cookies are stored encrypted (Windows DPAPI), cookies never follow redirects to another site, diagnostic reports hide cookies and tokens, the update helper accepts HTTPS only and verifies SHA-256.
- **English and Persian (فارسی)** with right-to-left windows, light and **dark "blue grey"** themes (toolbar button, or Options > General), tray app, optional portable mode (create an empty file `portable.mode` next to the exe). The browser extension's on-page video menu follows the same idea: a dark "premium" compact look and a light look, switching with the browser's colour scheme or a manual choice in the popup.

## Build (Windows, .NET 8 SDK)
```powershell
powershell -ExecutionPolicy Bypass -File .\build-release.ps1       # validates, publishes to .\publish
powershell -ExecutionPolicy Bypass -File .\build-installer.ps1     # ...and packs ONE Setup.exe (needs Inno Setup 6: winget install -e --id JRSoftware.InnoSetup)
```
Exit Makan first (tray icon > Exit). `build-release.ps1` refuses to publish when the source validation fails.

## Install
Run `MakanDownloadManager-16.0.0-Setup.exe` (per user, no administrator needed; "for all users" is offered). It connects Chrome, Edge, Brave and Firefox to Makan and adds Start menu entries. Then switch the extension on once in the browser (browsers do not allow a program to do that silently): see `EXTENSION-SETUP.txt` in the install folder.

Without the installer: run `install-browser-integration.ps1` from the publish folder **as yourself, not elevated** (it registers Makan for your Windows account), then load the extension as described in the extension README.
`install-browser-integration.ps1 -Check` shows exactly what is connected and what is not.

## Tests
`tests\run-all.ps1` (Windows) / `tests/run-all.sh` (Linux, macOS, Git Bash) run: source validation, the engine / bridge / queue / HLS / DASH / yt-dlp / V15 service tests against a mock web server, the extension logic and on-video button, and the full chain extension -> native host -> app. What they cannot see (WPF windows, SQLite/DPAPI on Windows, real browsers, real YouTube) is in `TESTING.md` and `FINAL_RELEASE_CHECKLIST.md`.

## Product
Created by **Makan A.D.** · [makanlab.tech](https://makanlab.tech)
