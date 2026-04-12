#!/usr/bin/env bash
set -euo pipefail

node /app/bot-service/src/server.js &
BOT_PID=$!

dotnet /app/SkinMarket.dll &
APP_PID=$!

cleanup() {
  kill "$BOT_PID" "$APP_PID" 2>/dev/null || true
}

trap cleanup INT TERM

set +e
wait -n "$BOT_PID" "$APP_PID"
EXIT_CODE=$?
set -e

cleanup
wait "$BOT_PID" 2>/dev/null || true
wait "$APP_PID" 2>/dev/null || true

exit "$EXIT_CODE"
