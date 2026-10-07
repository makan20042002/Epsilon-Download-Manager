# Source validation for the Windows build machine (build-release.ps1 runs it before publishing).
# The same checks exist for other machines in validate-source.js. Every condition is wrapped in its own script block and
# combined with parentheses: `Has a b -and Has c d` would pass "-and" to the function as an argument.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$version = (Get-Content -LiteralPath 'VERSION.txt' -Raw).Trim()
$failed = New-Object System.Collections.Generic.List[string]

function Read-Src([string]$path) { return (Get-Content -LiteralPath $path -Raw -Encoding UTF8) }
function Match-First([string]$path, [string]$pattern) {
  $m = [regex]::Match((Read-Src $path), $pattern)
  if ($m.Success) { return $m.Groups[1].Value } else { return '' }
}
function Check([string]$name, [scriptblock]$test) {
  $ok = $false
  try { $ok = [bool](& $test) } catch { $ok = $false }
  if ($ok) { Write-Host "PASS  $name" -ForegroundColor Green }
  else { Write-Host "FAIL  $name" -ForegroundColor Red; $script:failed.Add($name) }
}

Check "Version $version is used everywhere" {
  ((Match-First 'MakanDownloadManager/Branding.cs' 'Version = "([^"]+)"') -eq $version) -and
  ((Match-First 'MakanDownloadManager/Services/NativeBridge.cs' 'Version = "([^"]+)"') -eq $version) -and
  ((Match-First 'browser-extension/manifest.json' '"version":\s*"([^"]+)"') -eq $version) -and
  ((Match-First 'browser-extension-firefox/manifest.json' '"version":\s*"([^"]+)"') -eq $version) -and
  ((Match-First 'updater/Program.cs' 'ProductVersion = "([^"]+)"') -eq $version) -and
  ((Match-First 'installer/MakanDownloadManager.iss' '#define MyAppVersion "([^"]+)"') -eq $version)
}

Check 'One native-messaging identity everywhere' {
  $id = 'com.makan.downloadmanager'
  $files = @('MakanDownloadManager/Services/NativeBridge.cs', 'MakanNativeHost/Program.cs', 'install-browser-integration.ps1',
             'MakanDownloadManager/Services/WindowsIntegration.cs', 'browser-extension/background.js', 'browser-extension-firefox/background.js')
  $missing = @($files | Where-Object { -not (Read-Src $_).Contains($id) })
  $missing.Count -eq 0
}

Check 'Both extensions are Manifest V3 with a toolbar popup' {
  $a = Read-Src 'browser-extension/manifest.json' | ConvertFrom-Json
  $b = Read-Src 'browser-extension-firefox/manifest.json' | ConvertFrom-Json
  ($a.manifest_version -eq 3) -and ($b.manifest_version -eq 3) -and ($a.action.default_popup -eq 'popup.html') -and ($b.action.default_popup -eq 'popup.html')
}

Check 'The two extension copies are identical' {
  $differ = @('background.js', 'content.js', 'popup.js', 'i18n.js', 'popup.html' | Where-Object { (Read-Src ('browser-extension/' + $_)) -ne (Read-Src ('browser-extension-firefox/' + $_)) })
  $differ.Count -eq 0
}

Check 'All XAML files are well-formed XML' {
  $bad = @()
  foreach ($f in (Get-ChildItem 'MakanDownloadManager' -Filter *.xaml -Recurse)) {
    try { [xml](Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8) | Out-Null } catch { $bad += $f.Name }
  }
  $bad.Count -eq 0
}

Check 'Every named StaticResource exists in application, control, or window resources' {
  $app = Read-Src 'MakanDownloadManager/App.xaml'
  $controls = Read-Src 'MakanDownloadManager/Themes/ControlStyles.xaml'
  $global = New-Object 'System.Collections.Generic.HashSet[string]'
  foreach ($m in [regex]::Matches(($app + $controls), 'x:Key="([A-Za-z_]\w*)"')) { [void]$global.Add($m.Groups[1].Value) }
  $missing = @()
  foreach ($f in (Get-ChildItem 'MakanDownloadManager' -Filter *.xaml -Recurse | Where-Object { $_.FullName -notmatch 'Themes\\ControlStyles\.xaml$' })) {
    $text = Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8
    $available = New-Object 'System.Collections.Generic.HashSet[string]' ($global)
    foreach ($m in [regex]::Matches($text, 'x:Key="([A-Za-z_]\w*)"')) { [void]$available.Add($m.Groups[1].Value) }
    foreach ($m in [regex]::Matches($text, '\{StaticResource\s+([A-Za-z_]\w*)\}')) {
      if (-not $available.Contains($m.Groups[1].Value)) { $missing += ($f.Name + ': ' + $m.Groups[1].Value) }
    }
  }
  $missing.Count -eq 0
}

Check 'Every event handler named in XAML exists in its code-behind' {
  $missing = @()
  foreach ($f in (Get-ChildItem 'MakanDownloadManager' -Filter *.xaml -Recurse | Where-Object { $_.FullName -notmatch 'Themes' })) {
    $cs = $f.FullName + '.cs'
    if (-not (Test-Path -LiteralPath $cs)) { continue }
    $xaml = Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8
    $code = Get-Content -LiteralPath $cs -Raw -Encoding UTF8
    foreach ($m in [regex]::Matches($xaml, '\b(?:Click|Checked|Unchecked|SelectionChanged|TextChanged|MouseDoubleClick|Loaded|Drop|DragOver|Closing|KeyDown|PreviewKeyDown|ValueChanged|MouseLeftButtonDown|ContextMenuOpening|SizeChanged)="([A-Za-z_]\w*)"')) {
      $h = $m.Groups[1].Value
      if (-not [regex]::IsMatch($code, ('\b' + [regex]::Escape($h) + '\s*\('))) { $missing += ($f.Name + ': ' + $h) }
    }
  }
  $missing.Count -eq 0
}

Check 'No conflict markers, TODO/FIXME or NotImplementedException in production source' {
  $hits = @(Get-ChildItem 'MakanDownloadManager', 'MakanNativeHost', 'updater' -Recurse -Include *.cs | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
            Select-String -Pattern '<<<<<<<|>>>>>>>|\bTODO\b|\bFIXME\b|NotImplementedException')
  $hits.Count -eq 0
}

Check 'No C# event is assigned in an object initializer' {
  $hits = @(Get-ChildItem 'MakanDownloadManager' -Recurse -Include *.cs | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
            Select-String -Pattern '\{[^{};]*\bClick\s*=\s*[A-Za-z_(]')
  $hits.Count -eq 0
}

Check 'Release tooling and required files are present' {
  $need = @('installer/MakanDownloadManager.iss', 'updater/MakanUpdater.csproj', 'updater/Program.cs', 'tools/windows-production-test.ps1', 'build-release.ps1',
            'install-browser-integration.ps1', 'MakanDownloadManager/Themes/ControlStyles.xaml', 'MakanDownloadManager/IntelligentCenterWindow.cs',
            'MakanDownloadManager/Branding.cs', 'MakanNativeHost/Program.cs')
  $missing = @($need | Where-Object { -not (Test-Path -LiteralPath $_) })
  $missing.Count -eq 0
}

Check 'Security features: DPAPI cookies, redacted logs, HTTPS-only updater with SHA-256' {
  ((Read-Src 'MakanDownloadManager/Services/DownloadDb.cs').Contains('SecretProtector')) -and
  ((Read-Src 'MakanDownloadManager/Services/DiagnosticsService.cs').Contains('REDACTED')) -and
  ((Read-Src 'updater/Program.cs').Contains('SHA256')) -and
  ((Read-Src 'updater/Program.cs').Contains('HTTPS'))
}

if ($failed.Count -gt 0) { throw ('Source validation failed: ' + ($failed -join ', ')) }
Write-Host 'Source validation passed.' -ForegroundColor Green
