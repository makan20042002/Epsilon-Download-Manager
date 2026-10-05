# Epsilon Download Manager 1.4.1

This maintenance release improves download reliability, scheduling, and browser integration.

- Scheduled queues now follow the simultaneous-download limit from Options by default, with an optional per-queue override.
- New downloads can be assigned to any schedule queue, or create a new queue directly from the Add Download, batch, and video dialogs.
- The browser extension can send every highlighted link from the page and embedded frames as one batch.
- Smart connection learning is applied once instead of twice, avoiding unexpected speed reductions after server throttling.
- Torrent completion closes pending write handles before verification, preventing intermittent locked-file failures.
- The local test server accepts larger Windows connection bursts, preventing false HLS timeout failures in CI.
