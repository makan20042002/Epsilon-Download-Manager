# Epsilon Download Manager 1.6.0

Epsilon 1.6 introduces a new desktop design and strengthens the download engine while preserving in-place upgrades and existing user data.

## New design

- Replaced the large theme-colored header, permanent sidebar, and IDM-style icon ribbon with a single minimal Canvas workspace.
- Added compact All, Active, Finished, and Queues filters above the full-width download table.
- Kept every previous command available through the Canvas controls, row menu, optional navigation panel, and More panel.
- Added a dedicated gauge icon to the compact top speed limiter.
- Reduced oversized rounded elements in favor of restrained rectangular controls and download rows.
- Made Platinum Blue the default theme for new installations.
- Added the Makan Lab theme, based on makanlab.tech: ink black, electric blue, and amber.
- Added Makan Lab theme support to the Chrome/Edge and Firefox extension interfaces.
- Restored a compact top-bar speed limiter with Unlimited, quick presets, custom values, and combined/per-file modes.
- Replaced the old delete confirmation dialog with a three-choice contextual action bar.
- Completed downloads are now removed from Epsilon without deleting their finished files from disk.

## Engine and reliability

- Added optional Multi-Network segmented downloads across multiple connected adapters.
- A failing secondary network is dropped automatically and the download continues through a healthy route.
- Downloads wait for the internet connection to return without consuming their retry allowance.
- Strengthened resume-map durability by synchronizing data flushes with map snapshots.
- Made final file replacement atomic so an existing destination is not deleted before replacement succeeds.
- Uses independent HTTP/1.1 connections for segmented downloads where supported, with HTTP/2 fallback.

## Upgrade behavior

Install 1.6 normally over an older version. Settings, links, history, queues, and unfinished downloads are retained.
