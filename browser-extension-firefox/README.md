# Epsilon Download Manager — Firefox

This package is the Firefox browser integration for **Epsilon Download Manager 1.4.2**.

## Features
- Download interception with user-safe fallback: the browser transfer is only cancelled after Makan acknowledges the job.
- Context-menu downloads, media detection and download-all-links.
- Native messaging through `com.makan.downloadmanager`.
- English/Persian UI synchronization with the desktop application.
- HLS/DASH/media detection; YouTube qualities through yt-dlp (set up in Makan: Options > YouTube & other sites).
- **Alt+Shift+M** sends every link of the current page to Makan.

## Manifest
The Firefox package uses **Manifest V3**. Firefox supports MV3 background scripts, so this package intentionally uses `background.scripts` rather than a service worker.

The Chrome/Edge package is a separate MV3 service-worker build. Chrome has disabled Manifest V2 for current users, so the V15 Chrome/Edge package does not depend on MV2.

## Installation
1. Install Epsilon Download Manager 1.4.2.
2. Run `install-browser-integration.ps1` in a normal (not elevated) PowerShell window: it registers Makan for your own Windows account. The Setup.exe does this for you.
3. Open `about:debugging#/runtime/this-firefox` > *Load Temporary Add-on…* > choose `manifest.json` from this folder (temporary: Firefox removes it when it closes). A permanent install needs an add-on signed by Mozilla, or Firefox Developer Edition / ESR with `xpinstall.signatures.required` set to false and `extension-packages\makan-firefox.zip` opened as `.xpi`.
4. Restart Firefox after native-host registration if it was already running.
5. Use the Makan toolbar button or a download/context menu to test the connection.
