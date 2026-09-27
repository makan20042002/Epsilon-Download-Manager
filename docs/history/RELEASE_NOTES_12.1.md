# Makan Download Manager 12.1.0 — Final Polish

## Fixed
- Queue starvation caused by counting queued items as active slots.
- Repeated secure-cookie database rewrites on startup.
- SQLite backup/recovery edge cases around WAL/SHM files.
- SHA-256 verification loading entire large files into memory.
- Duplicate SQLite integrity-check execution in the dashboard.
- Download Basket disappearing after restart.
- Update manifest acceptance of insecure HTTP endpoints.

## Hardened
- Database backup is checkpointed before export.
- Recovery removes stale SQLite WAL/SHM sidecars.
- Update metadata requires HTTPS and a 64-character SHA-256 digest.

## Improved
- Download Basket can download all collected URLs, export JSON, and persist between launches.
- Source validation script added before the full Windows test suite.
