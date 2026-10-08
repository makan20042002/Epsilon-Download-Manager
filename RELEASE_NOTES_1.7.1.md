# Epsilon Download Manager 1.7.1

Version 1.7.1 is an in-place upgrade. Existing settings, download links, history, queues, partial files, and browser integration are preserved.

## Multi-Network reliability

- Fixed Windows adapter routing so a worker assigned to Ethernet, Wi-Fi, or tethering is explicitly routed through that interface instead of silently following the lowest-metric Windows route.
- Added a real transfer test for every selected adapter and per-network traffic labels in Download Details.
- Added controls for metered networks, daily usage, keeping one connection free, approval of newly detected networks, retry timing, and per-network colours.
- A failed adapter is temporarily removed from a download and retried later without failing the file.

## Downloading and queues

- Added expected SHA-256 verification and queue selection to Add Download.
- Added a persistent “When I delete” preference. Completed files remain protected from disk deletion.
- Completed downloads are removed from schedule queues while remaining in download history.
- Scheduled queues use the configured simultaneous-download count instead of a fixed value.
- Improved pause/restart recovery and visible disk-loading progress.

## BitTorrent

- Added BitTorrent protocol encryption (MSE/PE), IPv6 peer discovery from HTTP and UDP trackers, dual-stack incoming connections, persistent DHT contacts, and optional UPnP/NAT-PMP port mapping.
- Fixed upload back-pressure that could stall downloads.
- Torrent download and upload speed remain unlimited unless the user explicitly sets a limit.

## Interface and localization

- Restyled Add Download, Add Torrent, Batch, Video, Website Grabber, Download Details, Intelligent Center, Download Complete, and delete prompts to match the v6 desktop design.
- Added the full Multi-Network settings panel and refreshed Options navigation.
- Improved English/Persian coverage, RTL layout, and dark-theme readability.

## Packaging

- Chrome/Edge and Firefox extension packages are aligned to 1.7.1.
- The Mozilla-approved Firefox extension is available from [Firefox Add-ons](https://addons.mozilla.org/addon/epsilon-download-manager/).
- The installer upgrades older Epsilon versions in place and keeps all application data.

## Browser capture hotfix

- Fixed the Add Download window failing to open for downloads captured by Chrome, Edge, Brave, and Firefox.
- Added the missing shared display-font resource used by the redesigned secondary windows.
- Added validation that rejects undefined named XAML resources before a release is built.

## Speed test and official browser stores

- Fixed intermittent upload-test failures on slow connections. Epsilon now starts with a small upload sample, adapts the test size to the measured connection, allows slow transfers enough time, retries temporary server failures, and keeps a valid probe result if a larger follow-up request fails.
- Added direct buttons for the official Chrome Web Store and Firefox Add-ons listings to Getting Started and Options.
- Removed the old text-file instructions, Developer mode, unpacked Chrome installation, and temporary Firefox installation from the public installer.
- Kept the application and both browser extensions at version 1.7.1.
