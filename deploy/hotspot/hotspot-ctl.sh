#!/usr/bin/env bash
# Manual override for Wi-Fi hotspot/client switching — useful for testing the
# hotspot while at home, or forcing client mode.
# Usage: hotspot-ctl.sh {auto|force-hotspot|force-client|status}
set -euo pipefail

MODE_FILE="${STEPSOLVE_HOTSPOT_MODE_FILE:-/etc/stepsolve/hotspot-mode}"
HOTSPOT_SWITCH_SCRIPT="${STEPSOLVE_HOTSPOT_SWITCH_SCRIPT:-/usr/local/lib/stepsolve/deploy/hotspot/hotspot-switch.sh}"
WIFI_DEV="${STEPSOLVE_WIFI_DEV:-wlan0}"

usage() {
    echo "Usage: $0 {auto|force-hotspot|force-client|status}" >&2
    exit 1
}

[[ $# -eq 1 ]] || usage

case "$1" in
    auto|force-hotspot|force-client)
        mkdir -p "$(dirname "$MODE_FILE")"
        echo "$1" > "$MODE_FILE"
        echo "Mode set to '$1'; applying now..."
        bash "$HOTSPOT_SWITCH_SCRIPT"
        ;;
    status)
        echo "Mode: $(cat "$MODE_FILE" 2>/dev/null || echo auto)"
        echo "Active connection on $WIFI_DEV: $(nmcli -t -f GENERAL.CONNECTION dev show "$WIFI_DEV" 2>/dev/null | cut -d: -f2)"
        ;;
    *)
        usage
        ;;
esac
