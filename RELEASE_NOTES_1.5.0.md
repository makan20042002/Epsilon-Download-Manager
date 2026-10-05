# Epsilon Download Manager 1.5.0

This release improves download reliability, scheduling, deletion, themes, and Windows packaging.

- Completed downloads are automatically removed from schedule queues while remaining in download history.
- Delete and Delete Completed now ask whether to remove entries only or also erase finished files from disk.
- Restored the Dracula theme and gave Arctic Glass a distinct dark, icy palette instead of duplicating Platinum Blue.
- Reduced disk-related speed drops during segmented downloads and improved end-of-download connection utilization.
- HTTP downloads now prefer HTTP/2 with a safe HTTP/1.1 fallback.
- Torrent piece hashing and disk writes use a bounded asynchronous pipeline, keeping peer traffic responsive. Torrent speed remains unlimited unless the user sets a limit.
- Added Store/MSIX packaging support while retaining the normal in-place-upgrade installer that preserves settings, queues, history, links, and unfinished downloads.
- Browser extensions now follow the expanded app theme list.
