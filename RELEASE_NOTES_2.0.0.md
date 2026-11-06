# Epsilon Download Manager 2.0.0

Epsilon 2.0.0 installs over 1.7.1 and keeps the same program name, folder and installer ID. Your 1.7.1 settings are imported the first time it starts; the 1.7.1 data itself is never changed.

## What is new in this release

- **Six themes:** Arctic Blue (default), Jade Mint, Warm Ivory, Midnight Blue, Graphite Gold and Royal Plum. All use the same glass look; the Scheduler, Media Capture and History windows match the main window. Your saved theme is reset to Arctic Blue once after updating.
- **Cleaner main window:** the "Platinum" label is gone, the program is called "Epsilon Download Manager" everywhere, and compact rows were removed (one row size).
- **Column limits:** list columns (main table and the Scheduler's "Files in the queue") can no longer be dragged wider than their longest text.
- **Retry timing in seconds:** "Retry a dropped network after" accepts fractions of a minute (0.5 = 30 seconds).
- **Fix:** downloads no longer stop on their own with the status "Stopped" when a server answers slowly; they retry and, if they still fail, show a clear error.
- **Browser capture:** downloads from your own computer (localhost, 127.0.0.1, .test, .local) and from hosting control panels (cPanel, webmail) are left to the browser. A link you send with "Download with Epsilon" always works.
- **Portable version:** unzip anywhere; settings and downloads stay in the `data` folder beside the program.

## Files

| File | For |
|---|---|
| `EpsilonDownloadManager-2.0.0-Setup.exe` | Normal installation (also upgrades 1.7.1) |
| `EpsilonDownloadManager-2.0.0-Portable.zip` | No installation; run from any folder or USB stick |

SHA-256 checksums are in `SHA256SUMS.txt`. The installer is not code-signed, so Windows SmartScreen may warn on first run ("More info" > "Run anyway").

## Known limits

Local Peer Discovery, a live-play server for partly downloaded files, and remembering the Sequential option after a restart are not included yet.
