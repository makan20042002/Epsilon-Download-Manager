# Epsilon Download Manager 1.3.0

Version 1.3.0 focuses on dependable resumes and schedules, clearer progress reporting, browser-extension parity, and a cleaner Windows interface.

## Download and upgrading

- The installer upgrades previous Epsilon versions in place.
- Settings, download links, history, queues, and unfinished downloads are preserved.
- The application and extensions use the same 1.3.0 version number.

## Added

- Download sorting by newest/oldest, name, size, status, progress, and speed.
- Middle-click a download to open its containing folder.
- A second disk-loading progress bar when restoring an existing partial download.
- Per-file speed limits alongside the existing combined/global limiter.
- Lilac, Dracula, and Uhnohh themes, including matching browser-extension palettes.
- Browser context-menu support for sending selected/highlighted links as a batch.
- Clear download history action.
- An option to keep Windows awake while downloads are active.
- In-place installer upgrades that preserve application data.

## Fixed

- Downloads no longer stall at zero after the network speed changes mid-transfer.
- Resume correctly recovers partial HTTP, segmented, HLS, and yt-dlp downloads.
- Failed scheduled items can be retried after the queue has moved to later files.
- Stop All now stops the entire scheduled run instead of starting the next queued item.
- Scheduler and context-menu text remains readable in dark themes.
- Theme checkmarks no longer overlap theme names.
- YouTube size estimates from the extension are carried into the desktop download item.
- Torrent downloads are unlimited unless the user explicitly enables a limit.
- Browser-extension branding now consistently uses Epsilon Download Manager.

## Changed

- Removed duplicate scheduling controls from the More menu; the main toolbar remains the single place for Start All, Schedule, and Stop All.
- Custom progress-window caption buttons now use the standard Windows top-right position.
- Reduced rounded/oval styling throughout the desktop interface.
- The Firefox extension was submitted as a public Mozilla Add-ons listing.

## Verification

- Windows application build: passed.
- Source/version validation: passed.
- Browser-extension tests: 57 passed.
- Focused resume test: 6 passed, including byte-exact recovery without downloading the entire file again.
- Mozilla extension validation: 0 errors.

Installer SHA-256:

```text
F064547319EB8F6D590C9B3DF5B6788EAA64004B3FCC8940703F8208014B404F
```
