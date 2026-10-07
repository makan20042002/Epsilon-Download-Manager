# Epsilon Download Manager

[![Latest release](https://img.shields.io/github/v/release/makan20042002/Epsilon-Download-Manager?display_name=tag&sort=semver)](https://github.com/makan20042002/Epsilon-Download-Manager/releases/latest)
[![Windows CI](https://github.com/makan20042002/Epsilon-Download-Manager/actions/workflows/windows-ci.yml/badge.svg)](https://github.com/makan20042002/Epsilon-Download-Manager/actions/workflows/windows-ci.yml)
[![License: GPL v3](https://img.shields.io/badge/license-GPLv3-blue.svg)](LICENSE)
[![Windows 10/11](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6)](#requirements)

A fast, private download manager and BitTorrent client for Windows. Epsilon combines resumable multi-connection downloads, browser capture, scheduling, HLS/DASH media support, YouTube downloads, and a native torrent engine in one desktop application.

> Downloads never start silently. You choose **Start Download** or **Download Later**.

## Download

**[Download Epsilon Download Manager 1.7.1 for Windows](https://github.com/makan20042002/Epsilon-Download-Manager/releases/download/v1.7.1/EpsilonDownloadManager-1.7.1-Setup.exe)**

Run the installer normally. Version 1.7.1 upgrades an existing Epsilon installation in place and preserves settings, download links, history, queues, and unfinished downloads. You do not need to uninstall an older version first.

SHA-256:

```text
B6B72F32BB4D9E51D1CCBC11B81F4740EF9A4E8D34EAF1406C98C4D383A91E46
```

## Highlights

- **Fast, reliable downloads** — up to 16 connections, exact-byte resume after pause/restart, retry with back-off, checksum verification, and live handling of network-speed changes.
- **Multi-Network downloads** — optionally distribute segmented HTTP connections across connected Wi-Fi, Ethernet, or tethered networks, with automatic fallback when one link fails.
- **Flexible speed limits** — unlimited by default, a combined global limit, and optional independent per-file limits.
- **BitTorrent Engine 2.0** — magnet links and `.torrent` files, persistent DHT, PEX, HTTP/UDP trackers, HTTP web seeds, UPnP/NAT-PMP, file priority, seeding controls, and per-torrent bandwidth limits. Torrents have no artificial speed cap unless you set one.
- **Browser integration** — Chrome, Edge, Brave, and Firefox capture, context-menu commands, selected-link batches, page-link grabbing, and an on-video download button.
- **Media downloads** — native HLS and DASH plus YouTube and other supported sites through yt-dlp, including quality selection, audio extraction, playlists, and subtitles.
- **Scheduling and queues** — parallel queue limits, Start All/Schedule/Stop All controls, retryable failed items, and a complete schedule stop that does not advance to the next file.
- **Accurate resume feedback** — separate network and disk-loading progress, so restoring a partial download never looks frozen.
- **Redesigned desktop shell** — a compact productivity layout with a neutral title rail, left navigation, rectangular commands, and no large solid-color header or IDM-style icon ribbon.
- **Website-matched themes** — Platinum Blue is the default for new installs, and the new Makan Lab theme uses the live site’s ink, electric-blue, and amber palette. The browser extension follows the desktop theme.
- **Windows integration** — magnet and `.torrent` handlers, tray speed display, optional keep-awake while downloading, and in-place upgrades.
- **English and Persian** — full RTL-aware Persian localization.

See [the 1.7.1 release notes](RELEASE_NOTES_1.7.1.md) for the latest changes, or [the 1.7.0 release notes](RELEASE_NOTES_1.7.0.md) for the previous release.

## Browser extensions

The Windows installer registers Epsilon's native browser connection. Browsers still require the extension to be enabled once.

### Chrome, Edge, and Brave

1. Open the browser's extensions page (`chrome://extensions`, `edge://extensions`, or `brave://extensions`).
2. Enable **Developer mode**.
3. Select **Load unpacked** and choose the installed `browser-extension` folder.
4. Restart the browser and open the Epsilon toolbar button. It should show **Connected**.

### Firefox

Install the officially published Firefox extension from Mozilla Add-ons:

**[Epsilon Download Manager for Firefox](https://addons.mozilla.org/addon/epsilon-download-manager/)**

The Mozilla-signed store version remains installed after Firefox restarts. Development builds can still be tested temporarily from `about:debugging#/runtime/this-firefox` by choosing **Load Temporary Add-on** and selecting `browser-extension-firefox/manifest.json`.

## Requirements

- Windows 10 or Windows 11, 64-bit
- No separate .NET installation is needed by the self-contained installer
- Firefox 140 or newer for the Firefox extension
- yt-dlp/FFmpeg are optional and can be installed from **Options > YouTube & other sites**

## Privacy and security

- Browser cookies are encrypted at rest with Windows DPAPI.
- Authentication data is only forwarded to the local Epsilon native host for a user-requested download.
- Logs redact cookies, authorization headers, tokens, and URL signatures.
- Cross-origin redirects do not receive cookies from the original site.
- Update metadata and packages require HTTPS, and packages are verified with SHA-256.

Read the full [security policy](SECURITY.md).

## Build from source

Requirements: Windows, .NET 8 SDK, PowerShell, Node.js 18+, Python 3, and Inno Setup 6 for the installer.

```powershell
# Build and test the application and extensions
powershell -ExecutionPolicy Bypass -File .\tests\run-all.ps1

# Publish the self-contained application
powershell -ExecutionPolicy Bypass -File .\build-release.ps1

# Create the installer
powershell -ExecutionPolicy Bypass -File .\build-installer.ps1 -SkipBuild
```

## License and credits

Created by **Makan A.D.** and **Ohario**. Project website: [makanlab.tech](https://makanlab.tech).

Licensed under the [GNU General Public License v3.0](LICENSE).
