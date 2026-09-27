# Makan Download Manager – Chrome / Edge / Brave extension

## Install
1. Build/publish Makan and run `install-browser-integration.ps1` from the publish folder (once).
2. Open `chrome://extensions` (or `edge://extensions`), turn on **Developer mode**, click **Load unpacked** and choose this `browser-extension` folder.
   The extension ID is fixed (`gnhdkknoelpaneocnbnkbjgbnkkflpgk`, from the `key` in `manifest.json`), so the installer already knows it.
3. Restart the browser and click the Makan toolbar icon: the status line should say **Connected** (or that Makan will start on the next download).

## What it does
- Takes over normal downloads (toggle in the popup). Makan first checks that it can fetch the link; only then is the browser's copy cancelled. If Makan can't (login page, offline…), the browser keeps its download.
- Sends your cookies, referrer and user agent so logged-in downloads work.
- Shows a **Download this video** button on top of playing videos (IDM style). Click it for the list of qualities (TS / MP4 for each), or "Download all". The same list is in the toolbar popup.
- Detects videos, audio and HLS streams on the page (badge number on the icon). Direct files download immediately; HLS streams open Makan's "save video" dialog (name + folder).
- "Find downloadable links" lists files linked from the page and downloads the ones you tick.
- Right-click a link, video, audio or image: **Download with Makan**.
- **Alt+Shift+M** sends every link of the current page to Makan (change the shortcut in `chrome://extensions/shortcuts`).
- On YouTube pages the video button lists the qualities read by yt-dlp (set it up once in Makan: Options > YouTube & other sites).
- Sites listed under "Don't take over these sites" always use the browser.

## Architecture
The extension talks to `MakanNativeHost.exe` (native messaging). It forwards each request over a named pipe to the running desktop app, starting it in the background if needed. The desktop app owns the queue and the download engine.

`background.js`, `popup.html` and `popup.js` are identical in `browser-extension-firefox`; keep the two copies in sync.
