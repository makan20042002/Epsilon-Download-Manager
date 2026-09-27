# Makan / Epsilon Download Manager

A fast, private, modern download manager & BitTorrent client for Windows 10/11 (WPF, .NET 8) with Chrome, Edge, Brave, and Firefox integration.

**Nothing ever starts by itself: you always choose Start Download or Download Later.**

---

## Key Features

- **Fast, Resumable Downloads:** Up to 16 connections into one pre-allocated file, exact-byte resume after pause, crash or restart, automatic retries with back-off (`Retry-After` honored), a **global Speed Limiter in the toolbar** plus per-download limits, and SHA-256 checksum verification.
- **BitTorrent Engine 2.0:** Magnet links and `.torrent` files, DHT and peer exchange (PEX), UPnP/NAT-PMP port mapping, per-file priority (Skip/Normal/High), intelligent piece verification, seeding ratio/time limits, and bandwidth allocation. Double-click any torrent for full details (files, peers, trackers).
- **5 High-Contrast Themes + Follow Windows:** Light, Orange, Makan (Default Dark), Obsidian (True Black), and Nebula (Dark Violet), all WCAG AA compliant.
- **Smart Connection Handling:** Automatically scales connection counts when a server throttles (`429` / `503`) and learns optimal connection limits across restarts.
- **Browser Integration:** Chrome, Edge, Brave, and Firefox extension with single-click capture, on-page video overlay button, *Download All Links* tool, and **Alt+Shift+M** shortcut.
- **Media Engine:** Native HLS (`.m3u8`, AES-128, fMP4, separate audio) and DASH (`.mpd`) streaming, plus **YouTube & 1000+ video sites via yt-dlp** (video qualities, audio-only extraction, subtitles).
- **IDM-Style Desktop Power:** Main queue with categories, Scheduler with parallel limits ("download N files at once"), batch wildcards (`1..10`), drop target, and an **Intelligent Center** dashboard.
- **Privacy & Security First:** Windows DPAPI encrypted cookie storage, no cross-domain cookie leakage, redacted logs, and HTTPS-only update delivery.
- **Localization:** Full English and Persian (فارسی) support with native RTL layout.

---

## Build & Run (.NET 8 SDK)

```powershell
# Build release binaries (output to .\publish)
powershell -ExecutionPolicy Bypass -File .\build-release.ps1

# Create standalone Setup.exe installer (requires Inno Setup 6)
powershell -ExecutionPolicy Bypass -File .\build-installer.ps1 -SkipBuild
```

---

## License & Credits

Created by **Makan A.D.** & **Ohario** · [makanlab.tech](https://makanlab.tech)

Licensed under the [GNU General Public License v3.0](LICENSE).
