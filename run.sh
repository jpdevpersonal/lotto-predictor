#!/usr/bin/env bash

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
API_DIR="$ROOT_DIR/backend/LottoPredictor.Api"
FRONTEND_DIR="$ROOT_DIR/frontend"
API_URL="http://localhost:5080"
UI_URL="http://localhost:5173"
API_PID=""
UI_PID=""

cleanup() {
    trap - EXIT INT TERM
    printf '\nStopping Lotto Predictor...\n'
    [[ -n "$UI_PID" ]] && kill "$UI_PID" 2>/dev/null || true
    [[ -n "$API_PID" ]] && kill "$API_PID" 2>/dev/null || true
    wait 2>/dev/null || true
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || {
        printf 'Required command not found: %s\n' "$1" >&2
        exit 1
    }
}

require_command dotnet
require_command node
require_command npm
require_command curl

port_is_in_use() {
    local host="$1"
    local port="$2"
    (echo >"/dev/tcp/$host/$port") >/dev/null 2>&1
}

if port_is_in_use 127.0.0.1 5080; then
    printf 'Cannot start Lotto Predictor: API port 5080 is already in use.\n' >&2
    printf 'Stop the existing process, or use the already-running app.\n' >&2
    exit 1
fi

if port_is_in_use 127.0.0.1 5173; then
    printf 'Cannot start Lotto Predictor: UI port 5173 is already in use.\n' >&2
    printf 'Stop the existing process, or use the already-running app.\n' >&2
    exit 1
fi

if [[ ! -d "$FRONTEND_DIR/node_modules" ]]; then
    printf 'Installing frontend dependencies...\n'
    npm --prefix "$FRONTEND_DIR" install
fi

trap cleanup EXIT INT TERM

printf 'Starting API at %s...\n' "$API_URL"
dotnet run --project "$API_DIR" --launch-profile http &
API_PID=$!

printf 'Waiting for API to be ready...\n'
for _ in {1..60}; do
    if curl --silent --fail "$API_URL/api/draws/latest" >/dev/null; then
        break
    fi
    sleep 1
done

if ! curl --silent --fail "$API_URL/api/draws/latest" >/dev/null; then
    printf 'API did not become ready at %s.\n' "$API_URL" >&2
    exit 1
fi

printf 'Starting frontend at %s...\n' "$UI_URL"
npm --prefix "$FRONTEND_DIR" run dev -- --host 127.0.0.1 &
UI_PID=$!

printf '\nLotto Predictor is running. Open %s\nPress Ctrl+C to stop both services.\n\n' "$UI_URL"
wait "$API_PID" "$UI_PID"