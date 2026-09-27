# Builds Makan Download Manager for Windows x64 into .\publish (run on Windows with the .NET 8 SDK installed).
# -FrameworkDependent: a much smaller exe (a few MB instead of well over 100 MB), for attaching directly to a GitHub
# Release or similar - trades that size for requiring the person running it to already have the .NET 8 Desktop Runtime
# installed (https://dotnet.microsoft.com/download/dotnet/8.0 - "Desktop Runtime", not just the smaller base "Runtime").
# The default (no switch) needs nothing extra installed and is what the Setup.exe installer should keep using.
param([switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
$root       = Split-Path -Parent $MyInvocation.MyCommand.Path
$app        = Join-Path $root 'MakanDownloadManager\MakanDownloadManager.csproj'
$nativeHost = Join-Path $root 'MakanNativeHost\MakanNativeHost.csproj'
$updater = Join-Path $root 'updater\MakanUpdater.csproj'
$publish    = Join-Path $root 'publish'

if (Get-Process -Name 'MakanDownloadManager' -ErrorAction SilentlyContinue) {
  throw 'Makan is still running. Exit it first (tray icon > Exit), then run this script again.'
}
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

& (Join-Path $root 'tools\validate-source.ps1')   # throws when a check fails

# Main desktop app (WPF cannot be trimmed - it relies too heavily on reflection/XAML for trimming to be safe).
# EnableCompressionInSingleFile shrinks the self-contained payload substantially (the whole .NET runtime is bundled
# in); it costs a one-time extraction to a per-user cache folder on first launch after each new build, not a
# per-launch cost, so this is a genuinely free size reduction for anyone downloading the built exe from GitHub.
if ($FrameworkDependent) {
  Write-Host 'Framework-dependent build: the published exe will be small, but needs the .NET 8 Desktop Runtime already installed on whatever machine runs it.' -ForegroundColor Yellow
  dotnet publish $app -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $publish
} else {
  dotnet publish $app -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $publish
}
if ($LASTEXITCODE -ne 0) { throw 'Publishing MakanDownloadManager failed.' }

# Tiny native-messaging relay the browsers launch. It must sit next to MakanDownloadManager.exe.
dotnet publish $nativeHost -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:PublishTrimmed=true -p:EnableCompressionInSingleFile=true -o $publish
if ($LASTEXITCODE -ne 0) {
  Write-Warning 'Trimmed publish of MakanNativeHost failed; retrying without trimming (larger file, same behaviour).'
  dotnet publish $nativeHost -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $publish
  if ($LASTEXITCODE -ne 0) { throw 'Publishing MakanNativeHost failed.' }
}

# Secure updater. It verifies HTTPS + SHA-256 and performs staged replacement with rollback.
dotnet publish $updater -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:PublishTrimmed=true -p:EnableCompressionInSingleFile=true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Publishing MakanUpdater failed.' }

# Browser extensions + installers travel with the app.
Copy-Item (Join-Path $root 'browser-extension')         (Join-Path $publish 'browser-extension')         -Recurse
Copy-Item (Join-Path $root 'browser-extension-firefox') (Join-Path $publish 'browser-extension-firefox') -Recurse
Copy-Item (Join-Path $root 'install-browser-integration.ps1') $publish
Copy-Item (Join-Path $root 'install-background.ps1')          $publish
Copy-Item (Join-Path $root 'installer\EXTENSION-SETUP.txt') $publish
Get-ChildItem $publish -Filter *.pdb | Remove-Item -Force

# Ready-to-share extension packages (a Firefox .xpi is just a zip).
$dist = Join-Path $publish 'extension-packages'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Compress-Archive -Path (Join-Path $root 'browser-extension\*')         -DestinationPath (Join-Path $dist 'makan-chrome-edge.zip') -Force
Compress-Archive -Path (Join-Path $root 'browser-extension-firefox\*') -DestinationPath (Join-Path $dist 'makan-firefox.zip')     -Force

# Sanity check: everything the installer and the browsers need is really there.
foreach ($file in 'MakanDownloadManager.exe', 'MakanNativeHost.exe', 'MakanUpdater.exe', 'install-browser-integration.ps1', 'EXTENSION-SETUP.txt', 'browser-extension\manifest.json', 'browser-extension-firefox\manifest.json') {
  if (-not (Test-Path (Join-Path $publish $file))) { throw "Build finished but $file is missing from $publish" }
}
$version = (Get-Content (Join-Path $root 'VERSION.txt') -Raw).Trim()
Write-Host "Makan Download Manager $version published to $publish" -ForegroundColor Green
Write-Host 'Next: run  .\build-installer.ps1 -SkipBuild  to pack everything into one Setup.exe, and  .\tools\windows-production-test.ps1  on a clean Windows machine.'
Write-Host 'For public distribution, Authenticode-sign the EXE and installer before publishing.'
