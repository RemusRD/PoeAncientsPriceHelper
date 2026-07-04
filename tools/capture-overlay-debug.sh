#!/usr/bin/env bash
set -euo pipefail

BUILD="${BUILD:-71}"
REMOTE_HOST="${REMOTE_HOST:-codex_ssh@192.168.0.16}"
SSH_KEY="${SSH_KEY:-$HOME/.ssh/codex_pc_ed25519}"
REMOTE_ROOT="${REMOTE_ROOT:-/C:/Users/richa/Documents/codex/ssh-debug/runeshape-current}"
OUT_DIR="${OUT_DIR:-/Users/richardydani/Documents/Codex/2026-06-17/the-poe-overlay-is-right-now/outputs/overlay-debug}"

REMOTE_SIDE="$REMOTE_ROOT/nae-$BUILD/resources/sidecar"
CAPTURE_ID="${CAPTURE_ID:-$(date -u +%Y%m%dT%H%M%SZ)-nae-$BUILD}"
LOCAL_DIR="$OUT_DIR/$CAPTURE_ID"

mkdir -p "$LOCAL_DIR"
request_file="$(mktemp -t nae-overlay-debug.XXXXXX.json)"
cat > "$request_file" <<JSON
{
  "id": "$CAPTURE_ID",
  "reason": "manual-sftp-debug"
}
JSON

echo "==> Requesting overlay debug capture from nae-$BUILD"
sftp -i "$SSH_KEY" -o IdentitiesOnly=yes -o BatchMode=yes "$REMOTE_HOST" <<SFTP
put "$request_file" "$REMOTE_SIDE/overlay-debug-request.json"
SFTP
rm -f "$request_file"

sleep "${WAIT_SECONDS:-3}"

echo "==> Pulling overlay debug bundle"
sftp -i "$SSH_KEY" -o IdentitiesOnly=yes -o BatchMode=yes "$REMOTE_HOST" <<SFTP
get "$REMOTE_SIDE/overlay-debug/latest.json" "$LOCAL_DIR/latest.json"
get "$REMOTE_SIDE/overlay-debug/$CAPTURE_ID/state.json" "$LOCAL_DIR/state.json"
get "$REMOTE_SIDE/overlay-debug/$CAPTURE_ID/monitor.png" "$LOCAL_DIR/monitor.png"
get "$REMOTE_SIDE/overlay-debug/$CAPTURE_ID/overlay.png" "$LOCAL_DIR/overlay.png"
get "$REMOTE_SIDE/overlay-debug/$CAPTURE_ID/replay.html" "$LOCAL_DIR/replay.html"
SFTP

echo "==> Overlay debug bundle: $LOCAL_DIR"
