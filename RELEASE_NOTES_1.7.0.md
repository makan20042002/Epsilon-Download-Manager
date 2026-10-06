# Epsilon Download Manager 1.7.0

Version 1.7.0 is an in-place upgrade. Existing settings, download links, history, queues, partial files, and browser integration are preserved.

## New interface

- Rebuilt the main window around the approved Epsilon v6 design, with the real logo, compact menu bar, clean icon-and-label commands, filters, search, and a more visible status footer.
- Added a dedicated gallery containing all 12 themes.
- Added live Multi-Network and Speed Limiter panels to the main toolbar.
- Added filter counts, direct sorting, row checkboxes, Ctrl+A, Explorer-style drag selection, and a selection action bar.
- Added file/video/torrent type labels and seeding ratio status in the download list.

## Download management

- Added three explicit delete choices: remove from the list, erase data but keep the entry, or remove everything.
- Completed downloads are always kept on disk when removed from Epsilon.
- Added a command to reset a remembered delete choice.
- Added queue assignment, no-queue/start-now, and new-queue creation to both selection and context menus.
- The footer now shows total download/upload speed, Multi-Network state, limiter state, browser connection, active count, and free disk space.

## Engine

- Tracker tiers are contacted in parallel and peer connection ramp-up is faster.
- Upload limiting no longer blocks download reception from the same peer.
- Added a persistent seed-time limit and live application of torrent settings.
- Fixed selecting a previously skipped torrent file after the other files completed.
- Added BEP 19 HTTP(S) web-seed acceleration for single-file torrents.
- Persisted DHT bootstrap contacts between runs for faster peer discovery after restart.
- Torrents remain unlimited unless the user sets a torrent bandwidth limit.

## Packaging

- The installer upgrades older Epsilon versions in place and keeps user data.
- Current Chrome/Edge and Firefox extension packages are included with the installer and release assets.
