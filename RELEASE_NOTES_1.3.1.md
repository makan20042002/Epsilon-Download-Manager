# Epsilon Download Manager 1.3.1

## Theme improvements

- The complete identity/status bar now uses each theme's primary accent colour.
- Header titles and connection status remain readable through dedicated high-contrast colours.
- Download-list metadata, including **Last Try**, now uses the same readable foreground as file names.
- Lilac now uses a lavender-gray palette based on `#8C8EC6`, `#D7DDBB`, `#E453A0`, and `#760E6C`.
- Uhnohh is now a genuinely dark, neutral theme with vivid `#FFF700` yellow and hot orange/red accents.
- The Chrome/Edge and Firefox popup and video overlay follow the revised palettes.

## Browser capture reliability

- Browser downloads are paused while Epsilon accepts the handoff, avoiding the visible browser-download race.
- A rejected or unavailable handoff resumes the same browser download automatically.
- Redirected downloads use the browser's final URL when it is available.
- Slow preflight checks no longer reject valid large downloads after eight seconds. Epsilon accepts the job and lets its normal retry-capable downloader perform the real probe.
