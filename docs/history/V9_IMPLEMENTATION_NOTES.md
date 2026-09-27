# V8.1 implementation pass

Implemented in this package:

- Adaptive connection selection based on object size and range capability.
- Safer native messaging reads using `ReadExactly` for the 4-byte frame length.
- HLS manifest variant discovery with resolution/codec/bitrate metadata.
- DASH representation discovery with resolution/codec/bitrate metadata.
- Media stream selection UI and FFmpeg extraction.
- Firefox WebExtension package using the existing native host.
- Dashboard search and status filters.
- Diagnostic ZIP export containing the report and recent logs.
- V8.1 version alignment for the desktop app and browser extension.

Known limitation: a Windows/.NET 8 SDK was not available in the inspection environment, so a real WPF Release build could not be executed here. The source was statically checked and packaged for Windows build/test.
