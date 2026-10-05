param(
  [Parameter(Mandatory = $true)][string]$IdentityName,
  [Parameter(Mandatory = $true)][string]$Publisher,
  [string]$PublisherDisplayName = 'Makan Lab',
  [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$versionText = (Get-Content (Join-Path $root 'VERSION.txt') -Raw).Trim()
if ($versionText -notmatch '^(\d+)\.(\d+)\.(\d+)$') { throw "VERSION.txt must contain major.minor.patch, got '$versionText'." }
$packageVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"

if (-not $SkipBuild) { & (Join-Path $root 'build-release.ps1') }
$publish = Join-Path $root 'publish'
if (-not (Test-Path (Join-Path $publish 'MakanDownloadManager.exe'))) { throw 'The release publish folder is missing. Run without -SkipBuild.' }

& (Join-Path $root 'tools\generate-msix-assets.ps1')

$stage = Join-Path $root 'msix-stage'
$output = Join-Path $root 'msix-output'
$workspace = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
if (Test-Path -LiteralPath $stage) {
  $resolved = [IO.Path]::GetFullPath($stage)
  if (-not $resolved.StartsWith($workspace, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe MSIX staging path.' }
  Remove-Item -LiteralPath $resolved -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stage, $output | Out-Null
Copy-Item (Join-Path $publish '*') $stage -Recurse -Force
Copy-Item (Join-Path $root 'msix\Assets') (Join-Path $stage 'Assets') -Recurse -Force

function XmlEscape([string]$value) { [Security.SecurityElement]::Escape($value) }
$manifest = Get-Content (Join-Path $root 'msix\AppxManifest.template.xml') -Raw
$manifest = $manifest.Replace('__IDENTITY_NAME__', (XmlEscape $IdentityName))
$manifest = $manifest.Replace('__PUBLISHER__', (XmlEscape $Publisher))
$manifest = $manifest.Replace('__PUBLISHER_DISPLAY_NAME__', (XmlEscape $PublisherDisplayName))
$manifest = $manifest.Replace('__VERSION__', $packageVersion)
[IO.File]::WriteAllText((Join-Path $stage 'AppxManifest.xml'), $manifest, (New-Object Text.UTF8Encoding($false)))

$makeAppx = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Filter makeappx.exe -Recurse -ErrorAction SilentlyContinue |
  Where-Object { $_.FullName -match '\\x64\\makeappx\.exe$' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeAppx) { throw 'MakeAppx.exe was not found. Install the Windows 10/11 SDK.' }

$package = Join-Path $output "EpsilonDownloadManager-$versionText-x64.msix"
& $makeAppx.FullName pack /d $stage /p $package /o
if ($LASTEXITCODE -ne 0) { throw 'MakeAppx failed.' }

Write-Host "Unsigned Store package ready: $package" -ForegroundColor Green
Write-Host 'Microsoft Store will sign it after certification. The Identity Name and Publisher must exactly match Partner Center.' -ForegroundColor Yellow
