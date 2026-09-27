# Makan Download Manager 11.0

## V11 stability/security release

### Fixed
- Queue downloads can no longer be started by the global scheduler; QueueService owns queue slots and enforces `MaxParallel`.
- Queue starts now explicitly grant a download slot through `DownloadManager.StartFromQueue()`.
- Queue scheduler exceptions are written to diagnostics instead of being silently discarded.
- Shutdown no longer waits synchronously for six seconds on every exit; it uses a bounded asynchronous shutdown path.

### Security
- Browser/session cookies stored in SQLite are protected with Windows DPAPI and are bound to the current Windows user.
- Existing plaintext cookie rows remain readable and are transparently encrypted on the next save.
- SQLite database integrity is checked at startup.
- A `.backup` copy is maintained before opening the database.

### V11 features
- Global search also matches category, status, queue and error text.
- Duplicate detector for URL/destination conflicts.
- SHA-256 verification helper.
- Rolling download statistics service (total, completed, failures, average and peak speed).
- Adaptive per-domain connection hints.
- Bandwidth schedule model for time-based limits.
- Signed-manifest-ready update service with SHA-256 verification.
- Smart category/folder suggestions.
- Automated SQLite/DPAPI tests.

### Validation note
This source package was prepared in an environment without the .NET SDK, so the Windows WPF executable could not be compiled here. Run `tests\run-all.ps1` on Windows with the .NET 8 SDK to perform the full release build and automated test suite.
