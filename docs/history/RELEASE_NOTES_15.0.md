# Makan Download Manager 15.0.0

V15 adds the requested intelligent-download and production-product layer on top of the V15 download engine.

## Included
- Adaptive per-server connection learning and server profile visibility.
- Local download statistics and observed transfer-rate metrics.
- Bandwidth profiles: Unlimited, Gaming, Work and Night.
- Health Center covering database, engine, network, storage, native browser host and optional tools.
- Crash-recovery state discovery using existing atomic state manifests.
- V15 diagnostic export integrated with the existing redaction-safe diagnostics service.
- V15 Intelligent Center with Overview, Server Intelligence, Bandwidth, Health, Statistics and Recovery tabs.
- Existing existing download-engine capabilities retained: segmented HTTP/HTTPS, resume, retry/backoff, queue/scheduler, browser integration, HLS/DASH, yt-dlp, FFmpeg, SHA-256 verification and signed-update verification helpers.

## Architecture principle
Core downloading remains fully functional without cloud AI. The intelligence layer is deterministic/local-first and uses learned server behavior instead of making unsupported claims about network performance.

## Final polish pass
- Unified application, installer, updater and browser-extension versioning at 15.0.0.
- Promoted the V15 Intelligent Center to a first-class toolbar destination.
- Reworked the Intelligent Center into a responsive premium dashboard with live metrics, health cards, server profiles, bandwidth cards and recovery states.
- Corrected local statistics so downloaded bytes include current completed/partial progress and average rate ignores zero-speed records.
- Added V15 source-validation gates for the Intelligent Center, services and release branding.

## Validation
Static source, XML/XAML, JSON and browser-JavaScript checks were run in this environment. A Windows build with the .NET 8 SDK is still required for final WPF compilation, installer execution, native-messaging registration and browser E2E validation.
