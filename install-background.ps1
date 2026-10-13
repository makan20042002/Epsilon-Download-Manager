# Starts Epsilon quietly in the tray when you sign in to Windows, so the browser extension always has something to talk to.
param([string]$InstallDir = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $InstallDir 'EpsilonDownloadManager.exe'
if (!(Test-Path $exe)) { $exe = Join-Path "$env:ProgramFiles\Epsilon Download Manager" 'EpsilonDownloadManager.exe' }
if (!(Test-Path $exe)) { throw "EpsilonDownloadManager.exe was not found. Run this from the published folder or pass -InstallDir." }
$startup = [Environment]::GetFolderPath('Startup')
$shortcutPath = Join-Path $startup 'Epsilon Download Manager.lnk'
$wsh = New-Object -ComObject WScript.Shell
$shortcut = $wsh.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe
$shortcut.Arguments = '--background'
$shortcut.WorkingDirectory = Split-Path -Parent $exe
$shortcut.Save()
Write-Host "Startup shortcut installed: $shortcutPath"
