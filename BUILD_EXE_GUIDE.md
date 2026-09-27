# How to build Makan Download Manager into an .exe

Keep this file in the project folder — it's the same two commands every time, on Windows.

## 1. One-time setup (only needed once per computer)

1. **.NET 8 SDK** — download and install: https://dotnet.microsoft.com/download/dotnet/8.0
   (get the **SDK**, not just the Runtime — you need it to build, not only to run).
   Check it worked by opening PowerShell and running:
   ```powershell
   dotnet --version
   ```
   It should print something starting with `8.`.

2. **Inno Setup 6** — only needed if you want the single installer `.exe` (not just the raw app files):
   ```powershell
   winget install -e --id JRSoftware.InnoSetup
   ```
   (or download it by hand: https://jrsoftware.org/isdl.php)

That's it — nothing else to install.

## 2. Building (every time you have new code)

Open PowerShell **in the project folder** (the one with `MakanDownloadManager.sln` in it) and run:

```powershell
.\build-installer.ps1
```

This one command does everything:
- checks the source for problems (`tools\validate-source.ps1`)
- builds the app, the browser-native-messaging host, and the updater
- copies in the browser extensions
- packs it all into **one Setup.exe** with Inno Setup

If you only want the raw app files (no installer, e.g. to test quickly) run just:
```powershell
.\build-release.ps1
```

## 3. Where the result is

| What you ran | Where it ends up |
|---|---|
| `build-release.ps1` | `.\publish\MakanDownloadManager.exe` (run this directly to test) |
| `build-installer.ps1` | `.\installer-output\MakanDownloadManager-<version>-Setup.exe` (this is the one to share/install) |

`build-installer.ps1` runs `build-release.ps1` for you first, so you never need to run both by hand — just run `build-installer.ps1` and wait.

## 4. Installing it

Run the `Setup.exe` from `installer-output`. It:
- installs per-user (no administrator needed — "for all users" is offered too)
- connects Chrome, Edge, Brave and Firefox to Makan
- adds Start Menu entries

Browsers won't let an installer silently turn an extension on, so the last step is always manual: open `EXTENSION-SETUP.txt` (also copied into the install folder) and flip the extension on in each browser you use — takes about 10 seconds per browser.

## 5. If something goes wrong

- **"Makan is still running. Exit it first."** — right-click the tray icon → Exit, then run the build again.
- **"...cannot be loaded because running scripts is disabled..."** — PowerShell is blocking the script. Run this once, then try again:
  ```powershell
  powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
  ```
- **"Inno Setup 6 was not found."** — install it (step 1 above), then run the build again. If it's installed somewhere unusual, you can still build without it using `.\build-release.ps1` alone (you just won't get the single Setup.exe).
- **Source validation fails** (`build-release.ps1` stops with an error about a specific file/rule) — that's a real problem in the code, not your setup. Paste the exact error back and it can be fixed.
- **A NuGet/package restore error** — you need an internet connection the first time you build (it downloads the .NET packages the project depends on); after that first successful build it's mostly cached.

## 6. Rebuilding after every change

Every time you get new code (a new zip, or edited files copied over the old ones):
1. Copy the new/changed files into this same project folder, overwriting the old ones.
2. Run `.\build-installer.ps1` again.
3. Install the new Setup.exe over the old install (it upgrades in place — no need to uninstall first).

No other steps needed — this is the same command every single time.
