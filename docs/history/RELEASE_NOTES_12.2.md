# Makan Download Manager 12.2.0 — Final Stabilization Release

This release is a cleanup and reliability pass over the V12 codebase. It does not add speculative features; it makes the shipped application and release process consistent.

## Fixed
- Removed the duplicate `V12Diagnostics` type that prevented the WPF application from compiling.
- Added the missing `PortableModeService` implementation used by the desktop application.
- Unified the desktop/native-host/browser version to 12.2.0.
- Replaced the legacy V8 named-pipe identity with a stable application pipe name shared by the desktop app and native host.
- Fixed the source-validation script so it checks the actual HTTPS helper and the real desktop project assumptions.
- Added source checks for duplicate diagnostics and portable-mode support.
- Updated the release feature matrix so it does not claim an unshipped in-app updater/rollback API.
- Added the WPF desktop project to the release test/build path.

## Preserved
- Segmented HTTP/HTTPS downloads and resume support.
- Retry/backoff and bandwidth controls.
- SHA-256 verification.
- SQLite persistence and recovery.
- DPAPI-protected browser cookies.
- Chrome/Edge/Brave/Firefox native messaging.
- HLS/DASH/yt-dlp/FFmpeg media pipeline.
- Queueing, scheduling, smart rules, basket and diagnostics.

## Release requirement
The final Windows executable must be built and smoke-tested on Windows with the .NET 8 SDK because this source-only environment does not contain the Windows Desktop SDK.
