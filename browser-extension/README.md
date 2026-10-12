# Epsilon Download Manager – Chrome / Edge / Brave extension

## Install

1. Install the desktop app from the [latest Epsilon release](https://github.com/makan20042002/Epsilon-Download-Manager/releases/latest). The installer registers the native browser connection automatically.
2. Install [Epsilon Download Manager from the Chrome Web Store](https://chromewebstore.google.com/detail/epsilon-download-manager/nglldicodleblllopbkgncogljbdldpd).
3. Restart the browser and click the Epsilon toolbar icon. Its status should say **Connected** (or that Epsilon will start with the next download).

Developer mode is only needed when testing an unpublished source build. For development, load this folder as an unpacked extension and run `install-browser-integration.ps1` from the published desktop-app folder. The fixed extension ID is `nglldicodleblllopbkgncogljbdldpd`.

## What it does
- Takes over normal downloads (toggle in the popup). Epsilon first checks that it can fetch the link; only then is the browser's copy cancelled. If Epsilon cannot handle it (for example, a login page or an offline connection), the browser keeps its download.
- Sends your cookies, referrer and user agent so logged-in downloads work.
- Shows a **Download this video** button on top of playing videos (IDM style). Click it for the list of qualities (TS / MP4 for each), or "Download all". The same list is in the toolbar popup.
- Detects videos, audio and HLS streams on the page (badge number on the icon). Direct files download immediately; HLS streams open Epsilon's "save video" dialog (name + folder).
- "Find downloadable links" lists files linked from the page and downloads the ones you tick.
- Right-click a link, video, audio or image: **Download with Epsilon**.
- **Alt+Shift+M** sends every link of the current page to Epsilon (change the shortcut in `chrome://extensions/shortcuts`).
- On YouTube pages the video button lists the qualities read by yt-dlp (set it up once in Epsilon: Options > YouTube & other sites).
- Sites listed under "Don't take over these sites" always use the browser.

## Architecture
The extension talks to `MakanNativeHost.exe` (native messaging). It forwards each request over a named pipe to the running desktop app, starting it in the background if needed. The desktop app owns the queue and the download engine.

`background.js`, `popup.html` and `popup.js` are identical in `browser-extension-firefox`; keep the two copies in sync.
