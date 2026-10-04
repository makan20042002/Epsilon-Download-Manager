<#
.SYNOPSIS  Connects Chrome, Edge, Brave and Firefox to Epsilon Download Manager (per-user, no admin rights needed).
.DESCRIPTION
  Registers the native-messaging host (MakanNativeHost.exe) for each browser, then CHECKS the result
  (registry entries, manifest files, and a real round-trip with MakanNativeHost.exe) and prints what it found.
  Run it from the published Makan folder (or the project folder that contains "publish").
  -Check      only run the checks, change nothing
  -Uninstall  remove the registration again
#>
param(
  [string]$InstallDir,
  [string]$ExtensionId = 'gnhdkknoelpaneocnbnkbjgbnkkflpgk',                   # fixed by the "key" in browser-extension\manifest.json
  [string]$FirefoxId   = 'epsilon-download-manager@makanlab.tech',  # must match browser-extension-firefox\manifest.json
  [switch]$Check,
  [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$HostName = 'com.makan.downloadmanager'
$dataDir  = Join-Path $env:LOCALAPPDATA 'MakanDownloadManager\native-host'
$chromeManifest  = Join-Path $dataDir "$HostName.chrome.json"
$firefoxManifest = Join-Path $dataDir "$HostName.firefox.json"
$targets = @(
  @{ Name = 'Chrome';  Key = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$HostName";                 Manifest = $chromeManifest },
  @{ Name = 'Edge';    Key = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$HostName";                 Manifest = $chromeManifest },
  @{ Name = 'Brave';   Key = "HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\$HostName";    Manifest = $chromeManifest },
  @{ Name = 'Firefox'; Key = "HKCU:\Software\Mozilla\NativeMessagingHosts\$HostName";                        Manifest = $firefoxManifest }
)

function Get-RegDefault([string]$key) {
  try { return (Get-ItemProperty -Path $key -ErrorAction Stop).'(default)' } catch { return $null }
}

function Find-InstallDir {
  $cwd = (Get-Location).Path
  $candidates = @($PSScriptRoot, (Join-Path $PSScriptRoot 'publish'), $cwd, (Join-Path $cwd 'publish'), (Join-Path $env:ProgramFiles 'Makan Download Manager'))
  foreach ($c in $candidates) { if ($c -and (Test-Path (Join-Path $c 'MakanNativeHost.exe'))) { return $c } }
  return $null
}

# Real round-trip: exactly what a browser does (4-byte length + JSON on stdin, same framing back on stdout).
function Test-HostExe([string]$exe) {
  $psi = New-Object System.Diagnostics.ProcessStartInfo $exe
  $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
  $p = [System.Diagnostics.Process]::Start($psi)
  try {
    $json = [Text.Encoding]::UTF8.GetBytes('{"kind":"ping"}')
    $len  = [BitConverter]::GetBytes([int]$json.Length)
    $in = $p.StandardInput.BaseStream
    $in.Write($len, 0, 4); $in.Write($json, 0, $json.Length); $in.Flush()
    $out = $p.StandardOutput.BaseStream
    $head = New-Object byte[] 4
    $t = $out.ReadAsync($head, 0, 4)
    if (-not $t.Wait(20000) -or $t.Result -lt 4) { return $null }
    $n = [BitConverter]::ToInt32($head, 0)
    if ($n -le 0 -or $n -gt 65536) { return $null }
    $body = New-Object byte[] $n; $got = 0
    while ($got -lt $n) {
      $r = $out.ReadAsync($body, $got, $n - $got)
      if (-not $r.Wait(20000) -or $r.Result -le 0) { return $null }
      $got += $r.Result
    }
    return [Text.Encoding]::UTF8.GetString($body)
  } finally { try { $p.StandardInput.Close() } catch {}; if (-not $p.WaitForExit(3000)) { try { $p.Kill() } catch {} } }
}

function Show-Status {
  $ok = $true
  Write-Host ''
  Write-Host 'Checking browser integration...'
  foreach ($t in $targets) {
    $value = Get-RegDefault $t.Key
    if (-not $value) { Write-Host ("  [MISSING] {0,-8} registry key not found: {1}" -f $t.Name, $t.Key) -ForegroundColor Red; $ok = $false; continue }
    if (-not (Test-Path $value)) { Write-Host ("  [BROKEN]  {0,-8} registry points to a file that does not exist: {1}" -f $t.Name, $value) -ForegroundColor Red; $ok = $false; continue }
    try { $m = Get-Content -Raw -Path $value | ConvertFrom-Json } catch { Write-Host ("  [BROKEN]  {0,-8} manifest is not valid JSON: {1}" -f $t.Name, $value) -ForegroundColor Red; $ok = $false; continue }
    if ($m.name -ne $HostName) { Write-Host ("  [BROKEN]  {0,-8} manifest name is '{1}', expected '{2}'" -f $t.Name, $m.name, $HostName) -ForegroundColor Red; $ok = $false; continue }
    if (-not (Test-Path $m.path)) { Write-Host ("  [BROKEN]  {0,-8} host program not found: {1}" -f $t.Name, $m.path) -ForegroundColor Red; $ok = $false; continue }
    Write-Host ("  [OK]      {0,-8} -> {1}" -f $t.Name, $m.path) -ForegroundColor Green
  }
  $exe = (Get-RegDefault $targets[0].Key)
  if ($exe -and (Test-Path $exe)) { $exe = (Get-Content -Raw $exe | ConvertFrom-Json).path } else { $exe = $null }
  if ($exe -and (Test-Path $exe)) {
    try { $reply = Test-HostExe $exe } catch { $reply = $null; Write-Host "  Host start error: $($_.Exception.Message)" -ForegroundColor Red }
    if ($reply) { Write-Host "  [OK]      MakanNativeHost.exe answered: $reply" -ForegroundColor Green }
    else { Write-Host '  [FAILED]  MakanNativeHost.exe started but did not answer (blocked by antivirus/SmartScreen? try right-click > Properties > Unblock).' -ForegroundColor Red; $ok = $false }
  }
  Write-Host ''
  if ($ok) { Write-Host 'Everything is registered. Fully close and reopen the browser, then click the Epsilon toolbar icon.' -ForegroundColor Green }
  else { Write-Host 'Something above is not OK. Copy this whole window and send it to get it fixed.' -ForegroundColor Yellow }
  return $ok
}

if ($Uninstall) {
  foreach ($t in $targets) { if (Test-Path $t.Key) { Remove-Item $t.Key -Recurse -Force } }
  if (Test-Path $dataDir) { Remove-Item $dataDir -Recurse -Force }
  Write-Host 'Browser integration removed.'
  return
}

if ($Check) { [void](Show-Status); return }

if (-not $InstallDir) { $InstallDir = Find-InstallDir }
if (-not $InstallDir) {
  throw ("MakanNativeHost.exe was not found. Looked next to this script, in its 'publish' folder, in the current folder and in Program Files.`n" +
         "Build first with build-release.ps1, then run this script from the 'publish' folder, or pass -InstallDir <folder>.")
}
$hostExe = Join-Path $InstallDir 'MakanNativeHost.exe'
if (-not (Test-Path $hostExe)) { throw "MakanNativeHost.exe was not found in $InstallDir" }
if (-not (Test-Path (Join-Path $InstallDir 'MakanDownloadManager.exe'))) { throw "MakanDownloadManager.exe must be in the same folder as MakanNativeHost.exe ($InstallDir)." }
$hostExe = (Resolve-Path $hostExe).Path

New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
$exeJson = $hostExe.Replace('\', '\\')
# Written without a byte-order mark: browsers reject native-host manifests that start with one.
$utf8 = New-Object System.Text.UTF8Encoding($false)

[IO.File]::WriteAllText($chromeManifest, @"
{
  "name": "$HostName",
  "description": "Epsilon Download Manager native messaging bridge",
  "path": "$exeJson",
  "type": "stdio",
  "allowed_origins": [ "chrome-extension://$ExtensionId/" ]
}
"@, $utf8)

[IO.File]::WriteAllText($firefoxManifest, @"
{
  "name": "$HostName",
  "description": "Epsilon Download Manager native messaging bridge",
  "path": "$exeJson",
  "type": "stdio",
  "allowed_extensions": [ "$FirefoxId" ]
}
"@, $utf8)

foreach ($t in $targets) {
  New-Item -Path $t.Key -Force | Out-Null
  Set-ItemProperty -Path $t.Key -Name '(Default)' -Value $t.Manifest
}

Write-Host "Registered $hostExe"
Write-Host "  Chrome/Edge/Brave extension ID: $ExtensionId"
Write-Host "  Firefox add-on ID:              $FirefoxId"
$ok = Show-Status
if (-not $ok) { exit 1 }
