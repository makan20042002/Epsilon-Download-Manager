# Makan Download Manager 15.1.0 — Production Release Checklist

## Source / engine
- [x] V13.1 segmented/resumable engine retained
- [x] Retry/backoff and bandwidth controls retained
- [x] Persistent adaptive server learning retained
- [x] HLS/DASH/FFmpeg/yt-dlp integrations retained
- [x] Crash-safe download state retained

## Product / UI
- [x] Header with live status (smart engine, browser connection)
- [x] Makan A.D. attribution
- [x] makanlab.tech attribution/link
- [x] Intelligent Center (one window) in light and dark theme

## Browser
- [x] Chrome/Edge extension version aligned
- [x] Firefox extension version aligned
- [x] Firefox extension on Manifest V3 with background scripts and its toolbar popup
- [x] Stable native messaging identity retained
- [ ] Install/test Chrome on clean Windows machine
- [ ] Install/test Edge on clean Windows machine
- [ ] Install/test Firefox on clean Windows machine

## Installer / updater
- [x] Inno Setup definition included
- [x] Secure staged updater included
- [x] Updater verifies and stages the package before stopping the installed app
- [x] HTTPS-only update metadata/package enforcement (redirects checked hop by hop)
- [x] SHA-256 package verification
- [x] Rollback on failed update
- [ ] Build installer with Inno Setup on Windows (`build-installer.ps1`)
- [ ] Authenticode-sign installer and executables
- [ ] Test upgrade from previous release
- [ ] Test clean uninstall/reinstall

## Windows verification
- [x] Automated source/package checks prepared
- [x] Windows production test script included
- [ ] Windows 10 x64 runtime test
- [ ] Windows 11 x64 runtime test
- [ ] Sleep/wake recovery test
- [ ] Network interruption/recovery test
- [ ] Disk-full failure test
- [ ] Large-file segmented download test
- [ ] Browser interception/fallback test
- [ ] Update/rollback test

**Release rule:** V15 is not a final public release until the unchecked Windows gates are executed on a real Windows build machine.

## Things only a Windows machine can confirm (please tick after trying)
- [ ] `build-release.ps1` finishes; `MakanDownloadManager.exe` starts, the main window looks right in **light and dark** theme
- [ ] Every window once in dark theme: Options, progress window, Scheduler, Links, Intelligent Center (combo boxes, tabs, menus, scroll bars)
- [ ] Toolbar > Intelligent Center: all nine tabs open; *Apply profile* changes the limit in Options > Connection; *Create backup…* writes a file
- [ ] A queued download started by hand from the progress window really starts
- [ ] Exit from the tray returns immediately with a download running, and the download continues after the next start
- [ ] One YouTube video after Options > YouTube & other sites > Download / update tools
