# Makan Download Manager 12.1 — Final Polish

This is the cleaned and hardened V12 release candidate. It keeps the V12 feature set while fixing queue starvation, secure-cookie migration churn, unsafe large-file hashing, SQLite backup/recovery edge cases, duplicate integrity checks, and basket persistence.

## Build and verify on Windows

Requires Windows 10/11 x64, .NET 8 SDK, PowerShell, Python 3 and Node 18+.

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\validate-source.ps1
powershell -ExecutionPolicy Bypass -File .\tests\run-all.ps1
powershell -ExecutionPolicy Bypass -File .\build-release.ps1
```

The source validator performs structural checks before the full .NET/extension test suite.

## Key hardening
- Queue MaxParallel counts only active transfers, never pending queued items.
- DPAPI protects browser/session cookies.
- Legacy plaintext cookies are migrated once instead of being re-encrypted on every startup.
- SHA-256 verification streams files instead of loading the entire file into RAM.
- SQLite backups checkpoint WAL before copying and remove stale WAL/SHM files during recovery.
- Update manifests and package URLs must use HTTPS.
- Download Basket persists between launches.
