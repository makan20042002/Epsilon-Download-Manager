# Windows production smoke/regression gate.
# Run from a Windows PowerShell 5.1/7 session on a clean Windows 10/11 x64 machine.
$ErrorActionPreference='Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = (Get-Content (Join-Path $root 'VERSION.txt') -Raw).Trim()
$fail = 0
function Check($name, [scriptblock]$test) {
  try { if (& $test) { Write-Host "PASS  $name" -ForegroundColor Green } else { Write-Host "FAIL  $name" -ForegroundColor Red; $script:fail++ } }
  catch { Write-Host "FAIL  $name :: $($_.Exception.Message)" -ForegroundColor Red; $script:fail++ }
}
Write-Host "Makan Download Manager $version Windows production gate" -ForegroundColor Cyan
Check '.NET 8 runtime' { [version](dotnet --version) -ge [version]'8.0.0' }
Check 'Windows x64' { [Environment]::Is64BitOperatingSystem }
Check 'Main project exists' { Test-Path (Join-Path $root 'MakanDownloadManager\MakanDownloadManager.csproj') }
Check 'Native host exists' { Test-Path (Join-Path $root 'MakanNativeHost\MakanNativeHost.csproj') }
Check 'Installer definition exists' { Test-Path (Join-Path $root 'installer\MakanDownloadManager.iss') }
Check 'Updater project exists' { Test-Path (Join-Path $root 'updater\MakanUpdater.csproj') }
Check 'Browser manifests match version' {
  $a=Get-Content (Join-Path $root 'browser-extension\manifest.json') -Raw | ConvertFrom-Json
  $b=Get-Content (Join-Path $root 'browser-extension-firefox\manifest.json') -Raw | ConvertFrom-Json
  $a.version -eq $version -and $b.version -eq $version
}

$publish = Join-Path $root 'publish-test'
if(Test-Path $publish){Remove-Item $publish -Recurse -Force}
Write-Host 'Publishing test build...' -ForegroundColor Yellow
dotnet publish (Join-Path $root 'MakanDownloadManager\MakanDownloadManager.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $publish
if($LASTEXITCODE -ne 0){throw 'Desktop publish failed.'}
dotnet publish (Join-Path $root 'MakanNativeHost\MakanNativeHost.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $publish
if($LASTEXITCODE -ne 0){throw 'Native host publish failed.'}
Check 'Desktop executable produced' { Test-Path (Join-Path $publish 'MakanDownloadManager.exe') }
Check 'Native host executable produced' { Test-Path (Join-Path $publish 'MakanNativeHost.exe') }

Write-Host 'Runtime scenarios to execute manually/with a test harness:' -ForegroundColor Cyan
@(
  'Start application and verify no startup crash',
  'Add HTTP and HTTPS downloads; pause/resume/cancel each',
  'Disconnect network during download and reconnect',
  'Restart Windows/app during an active download and verify recovery',
  'Verify ETag/Last-Modified resume behaviour',
  'Verify SHA-256 completion verification',
  'Verify global and per-download speed limits',
  'Verify queue and scheduler',
  'Verify Chrome + Edge + Firefox interception and fallback',
  'Verify HLS/DASH and yt-dlp workflows',
  'Fill the target disk and verify graceful failure',
  'Run installer, uninstall, reinstall, and verify browser-host registration',
  'Run updater against a signed release package and verify rollback on a bad hash'
) | ForEach-Object { Write-Host "  - $_" }

if($fail -gt 0){throw "$fail automated Windows gate(s) failed."}
Write-Host 'Automated Windows gate passed. Complete the runtime scenarios above before public release.' -ForegroundColor Green
