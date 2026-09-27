# Makan Download Manager V12.0 — Intelligent Edition

V12 is the major reliability, intelligence, security and UX release built on the V11 engine.

## Core
- Adaptive per-domain connection selection is now applied by the HTTP engine.
- QueueService remains the only authority that starts queue-owned downloads.
- Smart performance modes: Maximum Speed, Balanced, Server Friendly and Custom.
- Global search, duplicate detection and category-aware planning are retained and extended.

## Smart Download
- SmartDownloadAnalyzer chooses category, destination and connection profile.
- DownloadRuleEngine supports wildcard rules for category, folder, priority and connections.
- DownloadBasket stores URLs for later batch processing and JSON export/import.

## Dashboard
- New V12 Intelligent Download Center.
- Live active/queued/completed/failed statistics.
- Category breakdown.
- Smart URL planner.
- Download basket.
- SQLite integrity check.
- Database backup.
- Diagnostic report.
- Security status.

## Security
- Browser/session cookies use Windows DPAPI through SecretProtector.
- SHA-256 verification helpers are exposed to the V12 security center.
- Database integrity checks and backup/recovery remain enabled.

## Updates / portability
- Verified update download helper with SHA-256 verification.
- Rollback backup helper for signed release installers.
- Portable mode support through `portable.mode` or `MAKAN_PORTABLE`.

## Extensibility
- `IMakanProvider` and `IMakanDownloadProvider` provide the first V12 provider contracts for future plugin modules.

## Testing note
The repository contains the Windows source, tests and build scripts. A Windows machine with the .NET 8 SDK is required to compile and run the WPF application and full native/browser integration tests.
