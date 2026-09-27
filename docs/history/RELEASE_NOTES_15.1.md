# Makan Download Manager 15.1.0 — release notes

15.1.0 is the reviewed and completed V15: everything of V15.0 plus the fixes below. Behaviour changes are listed so nothing surprises you.

## Fixed
- **Build:** `V13DashboardWindow` assigned an event in an object initializer (compile error); the two "Intelligent Center" windows are now one (`IntelligentCenterWindow`). `validate-source.ps1` used `Has … -and Has …` without parentheses, which fails in PowerShell and would have blocked `build-release.ps1`; it is rewritten (a Node twin, `validate-source.js`, runs on any machine).
- **Queues:** a download that belongs to a queue and is started by hand (progress window *Start*, retry) while its queue is stopped stayed *Queued* forever. Now only a **running** queue feeds its own files (and enforces "N files at the same time"); everything else is started by the normal scheduler.
- **Exit:** `App.OnExit` waited for shutdown on the UI thread while the shutdown awaited that same thread (possible hang). `Dispose` no longer skips saving the position of running downloads (they are stored as Queued / Stopped again, as before).
- **Connections:** the first download from any server was limited to 4 connections whatever the setting. Now the setting is used; only a server that answers 429 / 503 gets fewer connections (remembered across restarts, recovers after clean downloads). New option: Options > Connection > *Smart connections*.
- **Rules:** `DownloadRuleEngine` never matched (pattern and file name were swapped); a rule without a priority no longer resets the download's priority.
- **Cookies at rest:** a DPAPI value that cannot be opened (other Windows user or machine) is dropped instead of being sent as a cookie and encrypted a second time.
- **Firefox:** the toolbar popup was lost when the manifest moved to Manifest V3; `version_name` (unknown to Firefox) removed.
- **Updater:** it could not download from GitHub or any CDN (redirects were refused); now HTTPS redirects are followed and checked by hand. Files are swapped one by one with full rollback instead of renaming the install folder (which failed while the updater itself was running and would have hidden Makan's portable `data` folder). It builds as plain `net8.0`.
- **Installer:** one script (`installer/MakanDownloadManager.iss`, `build-installer.ps1`): per-user by default, browser registration always for the person who installs (the old script registered it for the administrator account), start-with-Windows option, stops a running Makan, removes the browser registration on uninstall, asks before deleting settings. Build files (`.iss`, test script) are no longer shipped inside the install folder.
- **Intelligent Center:** follows the light / dark theme, health checks are real (browser registration, tools installed by Makan, disk space, database integrity), optional tools are hints instead of warnings, no integrity check every second, live numbers update in place. The header pills show the real state (smart engine on/off, browser connected or not).
- **Portable mode:** logs and tools follow the portable data folder.
- Dead options (`AutoUpdate`, `DatabaseBackups`, `ShowStatistics`, `PortableMode`, `PerformanceMode`) removed from the settings class.
- Persian translations for every new text; the translation completeness test passes.

## Notes
- The updater and `UpdateService` are helpers: the application does not check for updates by itself yet.
- Bandwidth profile "Night" is unlimited by default (use it with a queue schedule); edit the profiles by saving your own list.
- The address `makanlab.tech.in` (Branding.cs, main window, extension popup) is kept as delivered: change `Branding.cs` and `browser-extension/popup.html` (both extension copies) if your address is different.

## Older releases
See `docs/history`.
