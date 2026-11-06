# Epsilon Download Manager — Project Overview

## Purpose

Epsilon Download Manager is the next major Windows release. It is intended to improve the interface and download features while remaining separate from the stable Epsilon 1.7.1 installation. V1 data may be imported, but V2 must not modify or remove the V1 installation or its data.

The source code is the authority. This file is a short orientation document, not a substitute for inspecting the implementation and tests.

## Product identity

- Product: **Epsilon Download Manager**
- Current source version: **2.0.0**
- Main executable: `EpsilonDownloadManager.exe`
- Native host: `EpsilonNativeHost.exe`
- Updater: `EpsilonUpdater.exe`
- V2 data directory: `%LOCALAPPDATA%\EpsilonDownloadManager2`
- Legacy V1 data directory: `%LOCALAPPDATA%\MakanDownloadManager`
- Identity constants: `MakanDownloadManager/ProductIdentity.cs`
- V1 migration logic: `MakanDownloadManager/Services/V1MigrationService.cs`

## Folder map

- `MakanDownloadManager/` — WPF desktop application, UI, models and download services.
- `MakanNativeHost/` — browser native-messaging relay.
- `updater/` — secure updater application.
- `browser-extension/` — Chromium extension source.
- `browser-extension-firefox/` — Firefox extension source.
- `tests/` — database, engine, bridge, server, extension and end-to-end tests.
- `installer/` — Inno Setup installer definition.
- `packaging/` — winget and Chocolatey packaging files.
- `tools/` — validation, signing and production-test scripts.
- `design/` — V2 design/reference material.
- `publish/` — generated release output; do not treat it as source.
- `installer-output/` — generated installers.

## Build and test

Prerequisites: Windows, .NET 8 SDK and Inno Setup 6 for the installer.

Build (one worker):

```powershell
dotnet build .\EpsilonDownloadManager.sln -m:1 -v:minimal --no-restore
```

Tests. EngineTests and DatabaseTests are console programs (OutputType Exe), not `dotnet test` projects. The owner runs them, not the coding tool:

```powershell
.\tools\validate-source.ps1
dotnet build .\tests\EngineTests -c Release -v q
dotnet .\tests\EngineTests\bin\Release\net8.0\EngineTests.dll
dotnet run --project .\tests\DatabaseTests\DatabaseTests.csproj -c Release
cd .\tests\extension; npm test
```

The full suite (needs the Python test server) is `tests\run-all.ps1`.

Build the release payload and installer only after tests pass:

```powershell
.\build-release.ps1
.\build-installer.ps1 -SkipBuild
```

Expected installer name: `installer-output\EpsilonDownloadManager-2.0.0-Setup.exe`.

## Implemented V2 work

- Separate V2 product identity, executable names, data directory, mutex and native pipe.
- V1 migration support designed to preserve the legacy database.
- Modern V2 main-window structure with sidebar, search, dashboard cards, filters and detailed download table.
- Resizable download columns with persisted widths.
- Per-download connection/segment setting supports up to 48; 8 is the default, 32 is the recommended high-performance setting and 48 is advanced.
- Configurable automatic retry attempts and retry window.
- YouTube playlist selection and saved format/quality preferences.
- Remote-control QR code and connection guide.
- Torrent details UI with overview, files, peers, trackers, pieces and logs, plus partial playback support where possible.
- Duplicate-download handling.
- Theme support.
- Browser-extension work for movable page controls and identifying multiple videos.
- Separate V2 installer identity and optional browser-integration registration.

## Work still requiring completion or verification

1. Compile the current working tree. The latest small XAML/code changes were made after the last confirmed successful build.
2. Run source validation and all relevant test projects. Record genuine failures separately from restricted-network or sandbox failures.
3. Audit all per-download connection clamps so they consistently allow 1–48. Do not confuse this with simultaneous-download or queue limits, which may remain 16.
4. Visually test the main window at minimum, normal and maximized sizes, including populated rows, column resizing, horizontal scrolling and persisted widths.
5. Verify the latest fixes for the Multi-Network dashboard text and wider sidebar.
6. Bring the detailed progress window closer to the approved reference, especially its speed graph, network breakdown, responsive layout and corner rendering.
7. Test real HTTP downloads, pause/resume, retry recovery and 8/32/48-connection behavior. More connections are not automatically faster on every server.
8. Test YouTube single-video and playlist downloads end to end without regressing the working 1.7.1 behavior.
9. Test torrents end to end: metadata, file selection, priorities, pieces, peers/trackers, resume and safe preview/playback of sufficiently downloaded media.
10. Test remote control and QR onboarding on another device on the same network.
11. Check Chromium and Firefox extension compatibility with both V1.7.1 and V2. Do not require a store replacement unless the native protocol or extension package truly changed.
12. Build a fresh private installer, test installation/uninstallation and coexistence with V1.7.1, then report its full path, size and SHA-256.

## Safety and release rules

- Preserve Epsilon 1.7.1 and its user data.
- Do not reset, discard or overwrite unrelated working-tree changes; the repository may already contain user work.
- Do not publish, upload, tag, create a release or replace store extensions without explicit user approval after testing.
- Do not use the existing installer as the final deliverable: it predates the latest source changes and must be rebuilt.
- Keep V2 and V1 browser integration compatible where possible. Back up and restore registration when the installer changes it.
- Be honest about untested features and environmental test limitations.

## Reference

The approved visual direction is based on `C:\Users\Makan\Desktop\Epsilon Download Manager UI Showcase.png`. Match its information architecture and polish while keeping real application behavior, accessibility and responsive sizing.
