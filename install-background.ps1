# Starts Makan quietly in the tray when you sign in to Windows, so the browser extension always has something to talk to.
param([string]$InstallDir = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $InstallDir 'MakanDownloadManager.exe'
if (!(Test-Path $exe)) { $exe = Join-Path "$env:ProgramFiles\Makan Download Manager" 'MakanDownloadManager.exe' }
if (!(Test-Path $exe)) { throw "MakanDownloadManager.exe was not found. Run this from the published folder or pass -InstallDir." }
$startup = [Environment]::GetFolderPath('Startup')
$shortcutPath = Join-Path $startup 'Makan Download Manager.lnk'
$wsh = New-Object -ComObject WScript.Shell
$shortcut = $wsh.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe
$shortcut.Arguments = '--background'
$shortcut.WorkingDirectory = Split-Path -Parent $exe
$shortcut.Save()
Write-Host "Startup shortcut installed: $shortcutPath"
