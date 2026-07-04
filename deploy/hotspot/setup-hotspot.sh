#!/usr/bin/env bash
# Idempotently creates or updates the stepsolve-hotspot NetworkManager AP profile,
# optionally a phone-hotspot fallback client profile, ranks any existing known
# Wi-Fi networks above the phone fallback, and seeds the default hotspot mode
# file if it doesn't exist.
# Override defaults with HOTSPOT_SSID / HOTSPOT_PASSWORD env vars.
# Set PHONE_HOTSPOT_SSID / PHONE_HOTSPOT_PASSWORD (and optionally
# PHONE_HOTSPOT_PRIORITY, default 5) to also register a fallback network,
# e.g. your phone's personal hotspot, ranked between home Wi-Fi and the Pi's
# own hotspot. Existing known networks (e.g. the home Wi-Fi profile created by
# Raspberry Pi Imager) are set to HOME_WIFI_PRIORITY (default 10) so they
# outrank the phone fallback.
set -euo pipefail

WIFI_DEV="${STEPSOLVE_WIFI_DEV:-wlan0}"
PROFILE="stepsolve-hotspot"
SSID="${HOTSPOT_SSID:-StepSolve}"
PASSWORD="${HOTSPOT_PASSWORD:-stepsolve1234}"
MODE_FILE="${STEPSOLVE_HOTSPOT_MODE_FILE:-/etc/stepsolve/hotspot-mode}"
PHONE_PROFILE="stepsolve-phone"
PHONE_PASSWORD="${PHONE_HOTSPOT_PASSWORD:-}"
HOME_WIFI_PRIORITY="${HOME_WIFI_PRIORITY:-10}"

if [[ ${#PASSWORD} -lt 8 ]]; then
    echo "HOTSPOT_PASSWORD must be at least 8 characters" >&2
    exit 1
fi

if [[ -n "${PHONE_HOTSPOT_SSID:-}" && ${#PHONE_PASSWORD} -lt 8 ]]; then
    echo "PHONE_HOTSPOT_PASSWORD must be at least 8 characters when PHONE_HOTSPOT_SSID is set" >&2
    exit 1
fi

if ! command -v nmcli &>/dev/null; then
    echo "nmcli not found — install NetworkManager first: sudo apt-get install -y network-manager" >&2
    exit 1
fi

if ! nmcli -t -f NAME connection show | grep -qx "$PROFILE"; then
    echo "==> Creating NetworkManager hotspot profile '$PROFILE'"
    nmcli connection add type wifi ifname "$WIFI_DEV" con-name "$PROFILE" autoconnect no ssid "$SSID"
else
    echo "==> Updating existing hotspot profile '$PROFILE'"
fi

nmcli connection modify "$PROFILE" \
    802-11-wireless.mode ap \
    802-11-wireless.band bg \
    802-11-wireless.ssid "$SSID" \
    ipv4.method shared \
    wifi-sec.key-mgmt wpa-psk \
    wifi-sec.psk "$PASSWORD" \
    connection.autoconnect no \
    connection.autoconnect-priority -10

echo "    Hotspot SSID: $SSID"

if [[ -n "${PHONE_HOTSPOT_SSID:-}" ]]; then
    PHONE_PRIORITY="${PHONE_HOTSPOT_PRIORITY:-5}"

    if ! nmcli -t -f NAME connection show | grep -qx "$PHONE_PROFILE"; then
        echo "==> Creating known-network profile '$PHONE_PROFILE' for phone hotspot fallback"
        nmcli connection add type wifi ifname "$WIFI_DEV" con-name "$PHONE_PROFILE" ssid "$PHONE_HOTSPOT_SSID"
    else
        echo "==> Updating existing phone hotspot profile '$PHONE_PROFILE'"
    fi

    nmcli connection modify "$PHONE_PROFILE" \
        802-11-wireless.ssid "$PHONE_HOTSPOT_SSID" \
        wifi-sec.key-mgmt wpa-psk \
        wifi-sec.psk "$PHONE_PASSWORD" \
        connection.autoconnect-priority "$PHONE_PRIORITY"

    echo "    Phone hotspot SSID: $PHONE_HOTSPOT_SSID (priority $PHONE_PRIORITY)"
else
    echo "==> No PHONE_HOTSPOT_SSID set — skipping phone hotspot fallback profile"
    echo "    Add one later with:"
    echo "    sudo env PHONE_HOTSPOT_SSID=... PHONE_HOTSPOT_PASSWORD=... bash $0"
fi

echo "==> Ranking existing known Wi-Fi networks above the phone fallback"
while IFS= read -r existing_profile; do
    [[ -z "$existing_profile" ]] && continue
    nmcli connection modify "$existing_profile" connection.autoconnect-priority "$HOME_WIFI_PRIORITY"
    echo "    $existing_profile -> priority $HOME_WIFI_PRIORITY"
done < <(nmcli -t -f NAME,TYPE connection show | awk -F: -v hs="$PROFILE" -v ph="$PHONE_PROFILE" '$2=="802-11-wireless" && $1!=hs && $1!=ph {print $1}')

echo "==> Seeding hotspot mode file"
mkdir -p "$(dirname "$MODE_FILE")"
if [[ ! -f "$MODE_FILE" ]]; then
    echo "auto" > "$MODE_FILE"
fi
echo "    Mode file: $MODE_FILE (current: $(cat "$MODE_FILE"))"
