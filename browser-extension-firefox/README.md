# Epsilon Download Manager — Firefox

This package is the Firefox browser integration for **Epsilon Download Manager 1.7.1**.

## Features
- Download interception with user-safe fallback: the browser transfer is only cancelled after Epsilon acknowledges the job.
- Context-menu downloads, media detection and download-all-links.
- Native messaging through `com.makan.downloadmanager`.
- English/Persian UI synchronization with the desktop application.
- HLS/DASH/media detection; YouTube qualities through yt-dlp (set up in Epsilon: Options > YouTube & other sites).
- **Alt+Shift+M** sends every link of the current page to Epsilon.

## Manifest
The Firefox package uses **Manifest V3**. Firefox supports MV3 background scripts, so this package intentionally uses `background.scripts` rather than a service worker.

The Chrome/Edge package is a separate MV3 service-worker build. Chrome has disabled Manifest V2 for current users, so the V15 Chrome/Edge package does not depend on MV2.

## Installation
1. Install the desktop app from the [latest Epsilon release](https://github.com/makan20042002/Epsilon-Download-Manager/releases/latest). The installer registers the native browser connection automatically.
2. Install the Mozilla-signed extension from [Firefox Add-ons](https://addons.mozilla.org/addon/epsilon-download-manager/).
3. Restart Firefox if it was already running.
4. Use the Epsilon toolbar button or a download/context menu to test the connection.

For development testing only, build the Windows app, run `install-browser-integration.ps1`, then open `about:debugging#/runtime/this-firefox`, choose *Load Temporary Add-on…*, and select `manifest.json` from this folder.
