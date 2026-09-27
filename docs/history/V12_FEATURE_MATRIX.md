# V12 Feature Matrix

| Area | V12 implementation |
|---|---|
| Adaptive connections | `AdaptiveConnectionService` is used by the HTTP segmented engine |
| Performance modes | `DownloadPerformanceMode` + `SmartDownloadAnalyzer` |
| Smart folders | `CategoryService` + `DownloadRuleEngine` |
| Smart priority | `DownloadRuleEngine` |
| Duplicate detection | `DuplicateDetector` |
| Search | Existing global list search + V12 planning |
| Download basket | `DownloadBasket` + V12 Center |
| Statistics | `StatisticsSnapshotBuilder` + V12 Center |
| Database recovery | Integrity/backup + V12 backup UI |
| DPAPI cookies | `SecretProtector` |
| SHA-256 | `DownloadSecurityService` + verified update helper |
| Portable mode | `portable.mode` or `MAKAN_PORTABLE=1` |
| Update verification | `UpdateService.CheckAsync` + `UpdateService.VerifyFileAsync` (HTTPS manifest only); no silent in-app installer is shipped. |
| Rollback preparation | SQLite backup/recovery is shipped; application binary rollback is handled by the installer/release process. |
| Plugin architecture | `IMakanProvider` / `IMakanDownloadProvider` |
| Browser integration | Chrome/Edge/Firefox bridge retained and versioned 12.2.0 |
| Scheduler | Existing queue scheduler + bandwidth schedule support |
| Notifications | Existing Windows tray/notification system |
| Drag & drop | Existing main-window drop target retained |
| Media | Existing HLS/DASH/yt-dlp/FFmpeg pipeline retained |
| Testing | Existing engine/database/bridge/extension tests + V12 feature tests |
