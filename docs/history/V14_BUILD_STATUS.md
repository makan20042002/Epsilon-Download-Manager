# V15 Build Status

- Version: 15.0.0
- Target: Windows x64 / .NET 8 WPF
- Branding: Makan A.D. / makanlab.tech.in
- Installer definition: `installer/MakanDownloadManager.iss`
- Updater: `updater/MakanUpdater.csproj`
- Windows validation: `tools/windows-production-test.ps1`

## Environment limitation
This package was prepared in a non-Windows environment without the .NET 8 SDK or Inno Setup compiler. Therefore this environment cannot truthfully mark WPF runtime, native-host registration, installer execution, or Windows browser E2E as passed. The included Windows scripts are the release gates to run on a Windows build machine.

## Final verification performed for this package

- V13.1 core service set present and compared against the V13.1 polished source.
- No unexpected changes were found in the core engine/services; intentional V15 changes are limited to release/UI/browser/versioning work.
- JSON manifests parse successfully.
- Browser JavaScript syntax checks pass with Node.js.
- XAML files parse as XML.
- V15 version, branding, browser-version consistency and production-marker scans pass.
- ZIP integrity was verified after packaging.

## Windows release gate

The environment used to prepare this package does not contain Windows, the .NET 8 Windows SDK, or Inno Setup. Therefore Windows runtime execution, WPF compilation, native-messaging registration, installer compilation, Authenticode signing, and browser E2E cannot honestly be marked passed here. `tools/windows-production-test.ps1` is included as the required Windows gate.


## V15 layer
V15 Intelligent Center, local statistics, bandwidth profiles, health checks, recovery discovery and V15 diagnostic export were added on top of the V15 engine. Final WPF/installer/browser runtime validation remains a Windows build step.
