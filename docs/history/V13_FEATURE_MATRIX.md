# V15 Feature Matrix

| Area | V15 implementation |
|---|---|
| HTTP/HTTPS | Segmented and single-stream engine |
| Resume | Range validation, ETag/Last-Modified, crash-safe state sidecar |
| Smart connections | Persistent per-server learning + adaptive throttling |
| Retry | Transient classification, backoff, Retry-After support |
| Bandwidth | Global and per-download limits |
| Browser | Chrome/Edge MV3 + Firefox native messaging |
| Browser safety | Acknowledge-before-cancel fallback |
| Browser media | HLS/DASH/media sniffing + quality selection |
| Link collection | Page/selection link picker |
| Media tools | FFmpeg + yt-dlp |
| Scheduling | Queue schedules, priority, finish actions |
| Security | DPAPI cookies, redirect cookie isolation, secret redaction, SHA-256 |
| Update security | HTTPS manifest + SHA-256 + optional RSA signature verifier |
| Diagnostics | Logs, report export, network snapshot |
| Recovery | SQLite WAL/integrity/backup + unfinished download recovery |
| UI | Dashboard, queues, history, basket, media and settings windows |
