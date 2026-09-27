# V15 build status

## Source/package validation completed
- JSON manifests parse successfully.
- Chrome/Edge and Firefox extension JavaScript passes Node syntax checks.
- Version is unified at 15.0.0.
- Native messaging identity is unified at `com.makan.downloadmanager`.
- V15 smart controller, resume manifest, diagnostics redaction and release verification components are present.
- No active V8 native-pipe identity remains.
- No TODO/FIXME/NotImplementedException markers remain in the main application/native-host source.

## Windows validation still required
This package targets WPF/.NET 8 on Windows. The build environment used to prepare this archive does not contain the .NET SDK or Windows Desktop SDK, so a native WPF Release build could not be executed here.

On Windows run:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\validate-source.ps1
dotnet build .\MakanDownloadManager.sln -c Release
powershell -ExecutionPolicy Bypass -File .\build-release.ps1
```

Then run `tests\run-all.ps1` for the full automated suite and perform the manual browser/media/recovery checks in `FINAL_RELEASE_CHECKLIST.md`.


## V15 layer
V15 Intelligent Center, local statistics, bandwidth profiles, health checks, recovery discovery and V15 diagnostic export were added on top of the V15 engine. Final WPF/installer/browser runtime validation remains a Windows build step.
