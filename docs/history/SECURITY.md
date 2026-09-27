# Security notes

- **Cookies** saved with a download are stored in the local database. They are sent only to the site they belong to: never to another host, not even after a redirect, and not to video segments served from another host.
- **Logs** contain error messages and file paths, not cookie values or request headers. Exported diagnostic reports contain no downloads' contents.
- **Browser extension:** it can read your cookies and see the addresses of pages you visit (needed to hand a logged-in download to Makan and to find videos). Everything stays on your PC: it talks only to the Makan app through the native host. It never sends data to any server. Sites can be excluded in the extension popup.
- **Native host:** `MakanNativeHost.exe` accepts only messages from the browser that started it and forwards them to the running app over a per-user named pipe. The app accepts HTTP/HTTPS addresses only.
- **Power-off from a queue** always shows a 60-second warning with a Cancel button first.
- The executables are **not code-signed**; Windows SmartScreen will warn. Build from source and keep the exes where only you can write.
- Register the native host only for the Windows user who uses Makan (`install-browser-integration.ps1` is per-user).

## V12.2 storage protection
Browser/session cookies persisted by the application are encrypted using Windows DPAPI and are scoped to the current Windows user. Existing plaintext cookie rows are accepted for backward compatibility and become encrypted when the item is saved.
