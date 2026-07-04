#!/usr/bin/env bash
# Switches wlan0 between the highest-priority known client network in range
# and the stepsolve-hotspot AP, honoring a manual mode override. Run
# periodically by stepsolve-hotspot-switch.timer, and directly by hotspot-ctl.sh.
set -uo pipefail

WIFI_DEV="${STEPSOLVE_WIFI_DEV:-wlan0}"
HOTSPOT_PROFILE="stepsolve-hotspot"
MODE_FILE="${STEPSOLVE_HOTSPOT_MODE_FILE:-/etc/stepsolve/hotspot-mode}"

log() {
    logger -t stepsolve-hotspot-switch "$1" 2>/dev/null || true
    echo "$1"
}

current_mode() {
    cat "$MODE_FILE" 2>/dev/null || echo auto
}

list_known_profiles() {
    nmcli -t -f NAME,TYPE connection show 2>/dev/null \
        | awk -F: -v hs="$HOTSPOT_PROFILE" '$2=="802-11-wireless" && $1!=hs {print $1}'
}

profile_priority() {
    nmcli -g connection.autoconnect-priority connection show "$1" 2>/dev/null || echo 0
}

profile_ssid() {
    nmcli -g 802-11-wireless.ssid connection show "$1" 2>/dev/null
}

list_visible_ssids() {
    nmcli -t -f SSID dev wifi list ifname "$WIFI_DEV" --rescan yes 2>/dev/null \
        | sed 's/\\:/:/g'
}

active_profile() {
    nmcli -t -f GENERAL.CONNECTION dev show "$WIFI_DEV" 2>/dev/null | cut -d: -f2
}

bring_up() {
    nmcli connection up "$1" ifname "$WIFI_DEV" >/dev/null 2>&1
}

# Prints the name of the highest-priority known profile currently in range
# (by connection.autoconnect-priority, higher wins), or nothing if none are.
pick_target() {
    local profile priority ssid_value ssid
    local -a known_profiles visible_ssids candidates
    mapfile -t known_profiles < <(list_known_profiles)
    mapfile -t visible_ssids < <(list_visible_ssids)

    candidates=()
    for profile in "${known_profiles[@]}"; do
        ssid_value="$(profile_ssid "$profile")"
        [[ -z "$ssid_value" ]] && continue
        for ssid in "${visible_ssids[@]}"; do
            if [[ "$ssid" == "$ssid_value" ]]; then
                priority="$(profile_priority "$profile")"
                candidates+=("${priority:-0}:$profile")
                break
            fi
        done
    done

    [[ ${#candidates[@]} -eq 0 ]] && return

    printf '%s\n' "${candidates[@]}" | sort -t: -k1,1 -rn | head -n1 | cut -d: -f2-
}

main() {
    local mode current target

    mode="$(current_mode)"
    current="$(active_profile)"

    case "$mode" in
        force-hotspot)
            if [[ "$current" != "$HOTSPOT_PROFILE" ]]; then
                log "Mode=force-hotspot; activating hotspot"
                bring_up "$HOTSPOT_PROFILE" || log "Failed to bring up hotspot"
            fi
            ;;
        force-client)
            target="$(pick_target)"
            if [[ -n "$target" && "$current" != "$target" ]]; then
                log "Mode=force-client; known network '$target' in range, switching from '${current:-none}'"
                bring_up "$target" || log "Failed to bring up '$target'"
            fi
            ;;
        *)
            target="$(pick_target)"
            if [[ -n "$target" ]]; then
                if [[ "$current" != "$target" ]]; then
                    log "Known network '$target' in range; switching from '${current:-none}'"
                    bring_up "$target" || log "Failed to bring up '$target'"
                fi
            else
                if [[ "$current" != "$HOTSPOT_PROFILE" ]]; then
                    log "No known network in range; activating hotspot"
                    bring_up "$HOTSPOT_PROFILE" || log "Failed to bring up hotspot"
                fi
            fi
            ;;
    esac
}

if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    main "$@"
fi
