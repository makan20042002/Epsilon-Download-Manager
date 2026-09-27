# Makan Download Manager 10.6 — release notes

## New in 10.6
- **YouTube (and other sites Makan cannot read itself) through yt-dlp.** On a YouTube page the video button now lists the qualities, audio only (M4A / MP3) and subtitle languages, like IDM. A YouTube link pasted or dropped into Makan opens a small window with the same list. The download is run by yt-dlp and supervised by Makan: progress, speed, pause / resume (partial files are continued), cancel (leftovers are removed), errors in plain words.
  - Set up once: *Options > YouTube & other sites > Download / update tools*. yt-dlp, Deno (runs YouTube's player code) and FFmpeg (joins video and sound) are stored in Makan's own folder (no admin rights) and checked against their published SHA-256 sums. yt-dlp updates itself when it is older than five days.
  - Optional: use your browser's login (Chrome / Edge / Firefox / Brave) for private or age-restricted videos.
  - Other pages where the extension finds no stream get a quiet second try through yt-dlp.
  - Only save videos you have the right to save; some sites forbid downloading in their terms.
- **IDM-style download progress window:** *Download status*, *Speed Limiter* (this download only; "Remember ... on stop/resume" keeps it), *Options on completion* (show the complete window; exit Makan; shut down / hibernate / sleep / restart with the 60-second warning; force), and *Show details* with the start-positions bar and one line per connection ("Send GET...", "Receiving data...", "Disconnect.", "Download complete."). Opens when a download starts (Options > Downloads) and on double-click of a running download; Cancel stops the download and closes the window.
- **Dark "blue grey" theme.** Options > General > Theme (Light / Dark / Follow Windows) and a *Theme* toolbar button; every window repaints at once, the title bar turns dark on Windows 10/11. The look of combo boxes, check boxes, tabs, menus, scroll bars and list headers is in `Themes/ControlStyles.xaml`; it is tested when Makan starts and, if anything is wrong, left out (the standard Windows controls are used then, and the reason is written to the log).
- Persian translations for everything new.

## Notes for updating
- New files: `Services/YtDlp.cs`, `Services/ToolsInstaller.cs`, `Services/ThemePalette.cs`, `ThemeManager.cs`, `Themes/ControlStyles.xaml`, `DownloadProgressWindow.xaml(.cs)`, `YouTubeDialog.xaml(.cs)`; `MakanDownloadManager.csproj` changed (the styles file is a Resource). Replace the whole project folder, not single files.
- Reload the browser extension (10.6.0).

## Tests
Engine / bridge / HLS / DASH / queue / yt-dlp / progress-window / theme checks, extension-logic and on-video-button checks and full-stack checks (`tests/run-all.sh`). yt-dlp is tested against a stand-in program, not against real YouTube. The WPF windows and the theme styles are **not** covered by automated tests: see `TESTING.md`.

## Known limits
Live streams and DRM are not supported; YouTube works only as long as the installed yt-dlp does (it updates itself); single-file DASH layouts are not supported yet; the exe is unsigned (SmartScreen warning); Firefox needs Mozilla-signed add-ons for permanent installs.

# 10.5 — release notes (previous release, rolled up)

## New in 10.5
- **Scheduler: "Download N files at the same time"** for every queue (1-16, like IDM), with icon buttons (move down / up / remove) and a *Time left* column. Downloads you start by hand keep their own limit (Options > Connection).
- **Options window like IDM's configuration:** *General* (browser status and repair, start with Windows, offer clipboard links, which browsers Makan takes over, language), *File types* (file types to take over incl. `R0*` wildcards, sites and addresses left to the browser), *Save to* (categories with their own file types and folders, "change folder to the last chosen", server file date, temporary folder for partial files), *Downloads* (ask/only-queue, download-complete window, queue choosers, ignore modification time on resume, duplicate link action, User-Agent for manual downloads, antivirus scan), *Connection*.
- **English and Persian (فارسی)** for the whole app (right-to-left windows in Persian) and for the browser extension (button on videos, selected-links bar, menus, popup). Choose in Options > General; restart to apply.
- Browser extension: downloads that Makan's options leave to the browser are no longer reported as errors; **Alt + click** on a link lets the browser download it (IDM's "prevent" key); things you pick yourself (video menu, context menu) ignore the file-type rules.
- Duplicate links are handled by your choice (ask / new name / overwrite / skip). Multi-part downloads always keep working: a queue decides how many of its files run at once.

# 10.4 — release notes (previous release, rolled up)

## 10.4
- **Modern look:** new design system (colours, fonts, rounded buttons, cards, roomy list rows with progress bars, icon toolbar, app icon), applied to every window.
- **IDM Tasks menu features:** Add batch download (wildcards, numbers/letters, zero-fill), Add batch download from clipboard, Run site grabber, Show drop target (floating icon for dropped links), Export / Import address lists as plain text files.
- **Select links -> "Download with Makan" bar** (like IDM's bottom-left bar): opens the picker with only the selected links. Several dropped / pasted / imported addresses now use the same picker window.
- **Errors are visible:** if a window cannot be opened (or the main window cannot be created) a message box explains why instead of nothing happening; details go to the log.

# 10.3 — release notes (previous release, rolled up)

## New in 10.3
- **Queues and Scheduler** (IDM-style): several queues, start by hand / at a time (once or weekdays) / on startup, stop time, per-file retries,
  "when done": open a file, exit Makan, shut down / hibernate / sleep / restart (60-second cancellable warning). "Download Later" parks items in a queue.
- **Download All Links window**: every link of a page with file name, type, size (checked in the background), link text and target folder; filters, Check All / Uncheck All, save-to modes; extension toolbar button and page context menu.
- **DASH in the video menu** now lists MP4 per quality (a full-stack test found it still pointed at the old Media window).
- Tree node **Queues**; right-click > *Add to queue*; toolbar *Start Queue* / *Stop Queue* / *Scheduler*.

## Since 10.0 (rolled up)
- Engine rewritten: one shared item list, chunked multi-connection downloads, exact-byte resume, retry policy, no cookie leaks across redirects.
- Native HLS and DASH downloader (AES-128, fMP4, byte ranges, separate audio, resume); FFmpeg optional for MP4 / merging.
- "Always ask" behaviour: Download File Info dialog, Start / Later, stopped after restart.
- IDM-style main window, categories, columns, light theme.
- Browser integration: tiny native host, per-browser installer with self-check, Chrome/Edge/Brave/Firefox extension with video button, page scan, quality list with length and size.

## Tests
255 engine/bridge/HLS/DASH/queue checks, 21 extension-logic checks, 15 on-video-button (jsdom) checks and 11 full-stack checks pass
(`tests/run-all.sh`). The WPF windows and the SQLite layer are **not** covered: see `TESTING.md`.

## Known limits
Live streams, DRM and sites without a downloadable stream (e.g. YouTube) are not supported; single-file DASH layouts are not supported yet;
the exe is unsigned (SmartScreen warning); Firefox needs Mozilla-signed add-ons for permanent installs.
