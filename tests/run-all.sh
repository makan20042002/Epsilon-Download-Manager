#!/usr/bin/env bash
# Runs every automated test suite (needs: .NET 8 SDK, Python 3, Node 18+). Used before each release.
#   engine + bridge + native host + HLS/DASH + queues/scheduler  (real C# sources against a mock web server)
#   extension logic, the on-video button (jsdom) and the full stack: extension -> MakanNativeHost -> app -> download
set -euo pipefail
cd "$(dirname "$0")"
python3 server/testserver.py 18080 > /tmp/makan-testserver.log 2>&1 &
SERVER=$!
trap 'kill $SERVER 2>/dev/null || true' EXIT
for i in $(seq 1 30); do curl -s -m 1 localhost:18080/__reset >/dev/null && break; sleep 0.5; done

echo "### source validation"
node ../tools/validate-source.js
WINDOWS=0; case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) WINDOWS=1;; esac
if [ "$WINDOWS" = 1 ]; then echo "### building full solution (WPF)"; dotnet build ../MakanDownloadManager.sln -c Release -v q
else echo "### WPF solution and SQLite/DPAPI tests need Windows: skipped on this machine"; fi
echo "### building test components"; dotnet build ../MakanNativeHost -c Release -v q; dotnet build ../updater -c Release -v q; dotnet build EngineTests -c Release -v q; dotnet build BridgeServer -c Release -v q
if [ "$WINDOWS" = 1 ]; then dotnet build DatabaseTests -c Release -v q; fi
echo "### engine / bridge / HLS / DASH / queues";   dotnet EngineTests/bin/Release/net8.0/EngineTests.dll
if [ "$WINDOWS" = 1 ]; then echo "### SQLite / DPAPI database"; dotnet DatabaseTests/bin/Release/net8.0-windows/DatabaseTests.dll; fi
echo "### extension logic + on-video button";        (cd extension && npm install --no-audit --no-fund --silent && npm test)
echo "### full stack (extension -> native host -> app)"; node e2e/e2e.test.js
echo; echo "ALL SUITES PASSED"
