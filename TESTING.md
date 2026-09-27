# Manual checklist for Windows (what the automated tests cannot see)

Run after `build-release.ps1`. Automated suites (`tests\run-all.ps1`) already cover the engine, bridge, extension logic and the full stack.

## 1. First start
- [ ] `MakanDownloadManager.exe` opens the IDM-style window (menu, toolbar, categories, columns). Close it with X: it stays in the tray; tray > Exit really quits.
- [ ] Starting it a second time only brings the first window to the front.
- [ ] `install-browser-integration.ps1` ends with "Everything is registered" (all lines OK, host answered).

## 2. Nothing starts by itself
- [ ] **Add URL** (Ctrl+N) with a big file: the *Download File Info* dialog appears; nothing downloads until *Start Download*.
- [ ] *Download Later* adds the file as **Stopped** with a **Q** mark; it does not start. Double-click on a stopped row does nothing.
- [ ] Start a download, pause it, close Makan from the tray, start it again: the item is **Stopped**, not running. *Resume* continues from where it stopped.
- [ ] Options: untick "Always ask" → downloads start at once; tick it again.

## 3. Browser
- [ ] Click a normal download link in the browser: the dialog shows size and the real file name; *Cancel* leaves nothing behind in Makan and the browser's own download is gone.
- [ ] A link that needs a login (already logged in in the browser) downloads correctly.
- [ ] Toolbar icon popup says **Connected** (or that Makan starts on the next download) and shows the extension version and whether the video button is shown.

## 4. Videos
- [ ] Open a page with a normal (non-DRM) video and press play. **Download this video** appears on the video (or top-right of the page).
- [ ] The list shows qualities (TS and MP4) with length, bitrate and an estimated size. Pick one: the save dialog appears, *Start Download* starts it, progress moves, the file plays.
- [ ] Without FFmpeg an MP4 request is saved as `.ts` and the row's Description explains why. With FFmpeg set in Options you get a real `.mp4`.
- [ ] *Download all* adds one MP4 per quality as **Stopped** items.

## 5. Download all links
- [ ] On a page with many file links, popup > **Download all links…** opens the window; sizes and types fill in after a few seconds.
- [ ] *Hide images*, *Hide web pages*, *Check All*, *Uncheck All* work; *Save to* radio buttons change the **Save to** column; the file name is editable.
- [ ] *Start Download* starts only the ticked rows; *Download Later* puts them in the chosen queue as stopped.

## 6. Queues and Scheduler
- [ ] Toolbar **Scheduler** opens the window. *New queue* > type a name > *Apply*. Right-click a download > *Add to queue* > that queue; it shows in the tree under **Queues** and in *Files in the queue*.
- [ ] **Start Queue** > choose the queue: its files run in order, one after another (Options: "simultaneous downloads" = 1 to see it clearly). **Stop Queue** stops them.
- [ ] Schedule test: set *Start download at* to two minutes from now (today's weekday ticked), *Apply*: the queue starts by itself at that minute, only once.
- [ ] *Stop download at* stops a running queue at that time without running the "when done" actions.
- [ ] "Exit Makan when done" quits after the last file. "Open the following file when done" opens it.
- [ ] Power-off test with a tiny download: tick *Turn off the computer* (Sleep) and run the queue: the **60-second countdown** appears; *Cancel* really cancels; leave *Force* unticked for the test.

## 7. Firefox
- [ ] `about:debugging#/runtime/this-firefox` > Load Temporary Add-on works; popup says Connected; a video button and a download capture work as above.
- [ ] After signing (see `browser-extension-firefox\README.md`) the add-on survives a Firefox restart.

## 8. Tasks menu (IDM features)
- [ ] **Add batch download…**: address `https://example.com/file*.zip`, numbers 1–5 with "Fill with zeros": the preview shows 5 addresses; OK opens the Download All Links window with them.
- [ ] **Add batch download from clipboard**: copy several links (one per line), the same window opens; with one link the normal *Download File Info* dialog opens.
- [ ] **Run site grabber…** with a page address lists the page's files and links (type "Image" rows are hidden by default).
- [ ] **Show drop target** shows a small blue icon; drag a link from the browser onto it: the dialog opens. It can be moved, remembers its place, double-click opens Makan, right-click hides it. Unchecking the menu item hides it.
- [ ] **Export > To text file** writes one address per line (selected downloads, or all when nothing is selected); **Import > From text file** reads them back into the picker window.

## 9. Selected links bar (browser)
- [ ] On a page with many download links, drag over some links: **Download with Makan (N links)** appears at the bottom-left. Click it: the picker opens with exactly those links. Clear the selection: the bar disappears. ✕ hides it for that selection.

## 10. Look and feel
- [ ] Window, dialogs (Download File Info, Download All Links, Scheduler, Options) all use the same fonts/colours; toolbar icons show as icons (not empty boxes); rows highlight on hover and selection; the Status column shows a thin progress bar while downloading.
- [ ] If a window ever fails to open you now get a message box with the reason (also in the log): send it along.

## 11. Options window
- [ ] **General:** the browser list shows ✓ for the browsers you set up; *Set up / repair* opens the installer script. Untick *Firefox* under "Take over downloads from these browsers", OK: a download in Firefox stays in Firefox (no red badge); tick it again.
- [ ] **File types:** remove `ZIP` from the list, OK: a .zip download in the browser stays with the browser. *Default* brings it back. Add your own site (e.g. `*.example.com`) to the sites list: downloads from it stay with the browser.
- [ ] **Save to:** pick *Video*, change its folder, OK: a video goes there. *New…* creates a category (e.g. Games, types `SAV PAK`); its files go to its folder. Set a **temporary folder**, start a big download: the parts are in that folder, the target folder stays clean until it finishes.
- [ ] **Downloads:** tick *Do not start downloading, only add files to the queue*: the info window only offers *Add to queue*. Add the same link twice with each *duplicate* option. Tick *antivirus scan* (Defender path is prefilled): after a download a scan runs silently.
- [ ] **Start with Windows** creates/removes the Run entry; *Offer to download links that I copy* opens the info window when you copy a link ending in .zip/.mp4 …

## 12. Scheduler
- [ ] Queue > *Files in the queue*: set *Download 2 files at the same time*, Apply, start the queue with 4 files: exactly 2 run at once.

## 13. Persian
- [ ] Options > General > Language > فارسی, OK, *Restart now*: menus, toolbar, dialogs, Options and Scheduler are Persian and mirrored (right-to-left); addresses and paths stay left-to-right. The browser popup, the button on videos and the selected-links bar are Persian too (after the first contact with Makan).


## 10.6 additions
- `tests/server/fake_ytdlp.py` imitates yt-dlp (`-J` info, progress template lines, resumable `.part` files, `[Merger]`, `ERROR:` + exit code); `YtDlpTests.cs` drives Makan's real code against it (Linux/macOS with python3; skipped on Windows). Real YouTube was not reachable from the test machine: try one video after installing the tools.
- `ProgressWindowTests.cs`: per-connection rows, position map, resume support, live speed limit, temporary vs remembered limit.
- `SettingsTests.Themes`: both palettes complete, contrast ratios, every `{DynamicResource X}` in the XAML exists, no theme colour is a `StaticResource`.
- **Not automated (check by eye on Windows):** the progress window, the YouTube dialog, the Options > YouTube tab and the dark theme in every window (open each once in dark mode; look at combo boxes, menus, tabs, list headers, scroll bars). If `Themes/ControlStyles.xaml` fails its start-up self-test, the Diagnostics report says so.


## 11.0 additions
- Queue ownership / MaxParallel: queued files must not start from the global scheduler; only QueueService may grant queue slots.
- Database integrity + recovery: corrupt `downloads.db` should restore from `downloads.db.backup` when a valid backup exists.
- DPAPI cookie protection: newly saved browser cookies must not appear as plaintext in SQLite.
- Database test suite: `tests\DatabaseTests`.
- Search: filename, URL, category, status, queue and error text.
- Shutdown: application exit should not wait for the previous six-second synchronous timeout.


## 15.1 additions
Automated: `V15Tests.cs` (rules, adaptive connections, server learning and persistence, bandwidth profiles, statistics, health checks, recovery scan, secrets and redaction, queue items started by hand, learned limit in the engine, state files, updater file swap with rollback and the updater's HTTPS refusals), manifest checks in `tests/extension/ext.test.js`, `tools/validate-source.js`.

Manual on Windows (not automatable here):
- [ ] **Intelligent Center** (toolbar): Overview numbers move while a download runs; Bandwidth > *Apply profile* sets Options > Connection > global limit and marks the profile *Active*; Health Center shows READY / OPTIONAL / CHECK sensibly (browser line matches `install-browser-integration.ps1 -Check`); Planner shows file name, category, folder and connections for a pasted address; Basket keeps its addresses after a restart and *Download all* adds them; Security: *Check database*, *Create backup…*.
- [ ] Header pills: *BROWSER CONNECTED* only when at least one browser is registered; *SMART ENGINE OFF* after unticking Options > Connection > Smart connections.
- [ ] Dark theme in every window (see FINAL_RELEASE_CHECKLIST.md).
- [ ] Installer: install for the current user, tick *Start with Windows*, reboot, Makan is in the tray; upgrade over an older version keeps the download list; uninstall removes the browser registration (`-Check` shows MISSING afterwards) and asks about settings.
- [ ] Updater against an HTTPS test manifest (a newer version number): app stops, files are replaced, app starts; a wrong SHA-256 leaves everything unchanged.
