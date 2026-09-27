# Makan Download Manager 14.0.0 — Production Edition

## Product direction
V14 turns the V13.1 download engine into a production-oriented Windows application with a premium desktop experience, installer/update infrastructure, hardened browser bridge, and repeatable Windows validation.

## Included
- V13.1 intelligent download engine retained: segmented downloads, resume validation, retry/backoff, bandwidth controls, persistent server learning, HLS/DASH, FFmpeg and yt-dlp integration.
- Premium desktop UI refresh with stronger visual hierarchy, status chips, modern cards, and clearer download actions.
- Chrome/Edge/Firefox browser integration with stable native messaging identity.
- Secure updater utility with HTTPS-only metadata, SHA-256 verification, staged replacement and rollback backup.
- Inno Setup installer definition with clean install/uninstall, native-host registration hook and browser integration payload.
- Windows smoke/regression test script covering startup, persistence, resume, browser bridge, security, and recovery scenarios.
- Security release checklist covering redirect boundaries, secret handling, update verification, extension permissions and file-system safety.
- Makan branding: Created by Makan A.D. · makanlab.tech.in.

## Verification note
Static source validation and package checks can run cross-platform. Final WPF build, installer compilation, browser registration and end-to-end Windows tests must be executed on Windows with the .NET 8 SDK installed.
