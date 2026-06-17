#!/usr/bin/env bash
# Extracts the P0 baseline metrics from the app's session logs.
# Run after a play session on Windows (git-bash) or copy the logs anywhere and point LOGDIR at them.
#
#   LOGDIR=/path/to/app/output ./tools/baseline.sh
#
# Reports min / median / max for the six baselines:
#   1. open -> first priced overlay
#   2. close -> overlay removal
#   3. remnant switch -> stale-price disappearance (detection lag bound)
#   4. idle CPU (panel closed)
#   5. active CPU (panel open)
#   6. OCR cycle duration
#
# key=value log lines; we pull a field with a small awk helper.
set -euo pipefail

LOGDIR="${LOGDIR:-.}"
LIFE="$LOGDIR/scan_lifecycle_log.txt"
PROF="$LOGDIR/scan_profile_log.txt"
CPU="$LOGDIR/cpu_log.txt"

# prints min/median/max of field 'key=val' across lines that contain 'match'
# usage: stat_for <file> <grep_match> <key>
stat_for() {
    local file="$1" match="$2" key="$3"
    local vals
    vals=$(grep "$match" "$file" 2>/dev/null \
        | grep -oE "$key=[0-9.]+" \
        | cut -d= -f2 \
        | sort -n || true)
    if [[ -z "$vals" ]]; then echo "  (no samples)"; return; fi
    local n min med max
    n=$(echo "$vals" | wc -l | tr -d ' ')
    min=$(echo "$vals" | head -1)
    max=$(echo "$vals" | tail -1)
    med=$(echo "$vals" | awk -v n="$n" 'NR==int((n+1)/2) || NR==int((n+2)/2){s+=$1;c++} END{printf "%.1f", s/c}')
    printf "  n=%s  min=%s  median=%s  max=%s\n" "$n" "$min" "$med" "$max"
}

echo "=== P0 baseline (LOGDIR=$LOGDIR) ==="
echo
echo "1. panel open -> first PRICED overlay (ms):"
stat_for "$LIFE" "event=view-complete" "firstPricedMs"
echo "   panel open -> first DISPLAY overlay (ms):"
stat_for "$LIFE" "event=view-complete" "firstDisplayMs"
echo
echo "2. panel close -> overlay removal: closeLagMs (detection lag):"
stat_for "$LIFE" "event=overlay-hidden" "closeLagMs"
echo
echo "3. remnant switch -> stale-price disappearance: pollIntervalMs (detection-lag bound):"
stat_for "$LIFE" "event=signature-changed" "pollIntervalMs"
echo "   prior stable period stableMs:"
stat_for "$LIFE" "event=signature-changed" "stableMs"
echo
echo "4. idle CPU (panel closed) - % of machine:"
stat_for "$CPU" "panelVisible=False" "cpuPctMachine"
echo
echo "5. active CPU (panel open) - % of machine:"
stat_for "$CPU" "panelVisible=True" "cpuPctMachine"
echo
echo "6. OCR cycle duration (ms):"
stat_for "$PROF" "status=scan-" "totalMs"
echo "   OCR stage only ocrMs:"
stat_for "$PROF" "status=scan-" "ocrMs"
echo
echo "Done."
