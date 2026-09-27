# Makan Download Manager V8

V8 is a reliability and product-quality release built on the V7.1 core.

## Core improvements
- Centralized retry policy with exponential backoff and bounded delays.
- Unique destination naming prevents accidental overwrite of an existing file or partial file.
- Native-browser jobs now enter the same queue path as desktop jobs.
- Diagnostic logging for retries, successful downloads, and failures.
- Crash recovery retained for interrupted downloads.
- Global and per-download bandwidth limiting retained.
- Safe resume with ETag / Last-Modified validation retained.
- Segmented downloads retain aggregate progress instead of competing writes.
- Atomic finalization and size verification retained.

## Browser
- Manifest updated to 8.0.0.
- Chrome/Edge MV3 native messaging bridge retained.
- Browser download is only cancelled after Makan acknowledges the job.
- Native jobs use the desktop download engine instead of becoming database-only jobs.

## Release model
V8 ships as source + Windows build/publish scripts. A Windows machine with the .NET 8 SDK is required to compile/publish the executable because this environment does not include the Windows desktop SDK.
