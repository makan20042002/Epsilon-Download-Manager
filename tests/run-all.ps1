# Windows version of run-all.sh (needs: .NET 8 SDK, Python 3, Node 18+ on PATH).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$python = if (Get-Command python -ErrorAction SilentlyContinue) { 'python' } else { 'python3' }
$server = Start-Process $python -ArgumentList 'server/testserver.py', '18080' -PassThru -WindowStyle Hidden
try {
  for ($i = 0; $i -lt 40; $i++) { try { Invoke-WebRequest http://127.0.0.1:18080/__reset -UseBasicParsing -TimeoutSec 1 | Out-Null; break } catch { Start-Sleep -Milliseconds 500 } }
  Write-Host '### source validation'
  & ..\tools\validate-source.ps1
  # validate-source.ps1 intentionally moves to the repository root; return here before using test-relative paths.
  Set-Location $PSScriptRoot
  Write-Host '### building full solution'
  dotnet build ..\MakanDownloadManager.sln -c Release -v q
  Write-Host '### building test components'
  dotnet build ..\MakanNativeHost -c Release -v q; dotnet build ..\updater -c Release -v q; dotnet build EngineTests -c Release -v q; dotnet build DatabaseTests -c Release -v q; dotnet build BridgeServer -c Release -v q
  Write-Host '### engine / bridge / HLS / DASH / queues'
  dotnet .\EngineTests\bin\Release\net8.0\EngineTests.dll; if ($LASTEXITCODE -ne 0) { throw 'engine tests failed' }; dotnet .\DatabaseTests\bin\Release\net8.0-windows\DatabaseTests.dll; if ($LASTEXITCODE -ne 0) { throw 'database tests failed' }
  Write-Host '### extension logic + on-video button'
  Push-Location extension; npm install --no-audit --no-fund --silent; npm test; if ($LASTEXITCODE -ne 0) { throw 'extension tests failed' }; Pop-Location
  Write-Host '### full stack (extension -> native host -> app)'
  node .\e2e\e2e.test.js; if ($LASTEXITCODE -ne 0) { throw 'full-stack tests failed' }
  Write-Host 'ALL SUITES PASSED' -ForegroundColor Green
} finally { if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force } }
