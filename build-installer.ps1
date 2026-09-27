<#
.SYNOPSIS  Builds Makan and packs everything into ONE installer:  installer-output\MakanDownloadManager-<version>-Setup.exe
.DESCRIPTION
  1. runs build-release.ps1 (validates the source, publishes the app, native host, updater and browser extensions into .\publish)
  2. compiles installer\MakanDownloadManager.iss with Inno Setup 6 (free; install once with:  winget install -e --id JRSoftware.InnoSetup)
  -SkipBuild  reuse the existing .\publish folder
#>
param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$publish = Join-Path $root 'publish'

if (-not $SkipBuild) { & (Join-Path $root 'build-release.ps1') }
if (-not (Test-Path (Join-Path $publish 'MakanDownloadManager.exe'))) { throw "Nothing to pack: $publish\MakanDownloadManager.exe is missing. Run without -SkipBuild." }

$candidates = @(
  (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
  (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
  (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
)
$iscc = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
  throw ("Inno Setup 6 was not found. Install it once (free) and run this script again:`n" +
         "  winget install -e --id JRSoftware.InnoSetup`n" +
         "or download it from https://jrsoftware.org/isdl.php")
}

Copy-Item (Join-Path $root 'installer\EXTENSION-SETUP.txt') $publish -Force
$version = (Get-Content (Join-Path $root 'VERSION.txt') -Raw).Trim()
& $iscc (Join-Path $root 'installer\MakanDownloadManager.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup reported an error (see above).' }

$setup = Join-Path $root "installer-output\MakanDownloadManager-$version-Setup.exe"
Write-Host ''
Write-Host "Installer ready: $setup" -ForegroundColor Green
Write-Host 'This one file installs Makan, connects the browsers and adds Start menu entries. Sign it before public distribution (tools\sign-release.ps1).'
