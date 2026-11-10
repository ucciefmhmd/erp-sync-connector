#!/usr/bin/env bash
# Starts the mock ERP and the connector for a local or Codespaces demo.
# Logs go to .demo-logs/; stop both with: pkill -f ErpSync
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p .demo-logs

if pgrep -f "ErpSync.Api" > /dev/null; then
  echo "Demo is already running."
  exit 0
fi

dotnet run --project tools/ErpSync.MockErp --launch-profile http > .demo-logs/mock-erp.log 2>&1 &
dotnet run --project src/ErpSync.Api --launch-profile http > .demo-logs/api.log 2>&1 &

for _ in $(seq 1 60); do
  if curl -fs http://localhost:5100/health/live > /dev/null 2>&1; then
    echo ""
    echo "  Connector API  : http://localhost:5100/scalar/v1"
    echo "  Demo API key   : dev-local-key-change-me (Development only)"
    echo "  Requests file  : requests.http"
    echo "  Logs           : .demo-logs/"
    exit 0
  fi
  sleep 2
done

echo "The API did not start; see .demo-logs/api.log" >&2
exit 1
