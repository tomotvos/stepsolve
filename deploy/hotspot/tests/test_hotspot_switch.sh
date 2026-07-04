#!/usr/bin/env bash
# Scenario tests for hotspot-switch.sh's decision logic, using a fake nmcli.
# Run: bash deploy/hotspot/tests/test_hotspot_switch.sh
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export PATH="$SCRIPT_DIR:$PATH"   # this directory's nmcli stub shadows the real nmcli
export NMCLI_UP_LOG
export STEPSOLVE_HOTSPOT_MODE_FILE
fail=0

run_scenario() {
    # $1 = mode file contents ("" means no override file, i.e. auto)
    NMCLI_UP_LOG="$(mktemp)"
    : > "$NMCLI_UP_LOG"
    STEPSOLVE_HOTSPOT_MODE_FILE="$(mktemp)"
    if [[ -n "$1" ]]; then
        echo "$1" > "$STEPSOLVE_HOTSPOT_MODE_FILE"
    else
        rm -f "$STEPSOLVE_HOTSPOT_MODE_FILE"
    fi

    (source "$SCRIPT_DIR/../hotspot-switch.sh"; main) >/dev/null

    up_calls="$(cat "$NMCLI_UP_LOG")"
    rm -f "$NMCLI_UP_LOG" "$STEPSOLVE_HOTSPOT_MODE_FILE"
    echo "$up_calls"
}

assert_eq() {
    local scenario="$1" expected="$2" actual="$3"
    if [[ "$expected" != "$actual" ]]; then
        echo "FAIL: $scenario — expected up-call '$expected', got '$actual'"
        fail=1
    else
        echo "PASS: $scenario"
    fi
}

# Scenario 1: home SSID in range, hotspot currently active -> switch to home
export FAKE_KNOWN_PROFILES="stepsolve-home=MyHomeWifi"
export FAKE_PROFILE_PRIORITIES=""
export FAKE_VISIBLE_SSIDS="MyHomeWifi\nSomeNeighborWifi"
export FAKE_ACTIVE_PROFILE="stepsolve-hotspot"
assert_eq "home in range, hotspot active" "stepsolve-home" "$(run_scenario '')"

# Scenario 2: home SSID not in range, hotspot already active -> no-op (already correct)
export FAKE_VISIBLE_SSIDS="SomeNeighborWifi"
export FAKE_ACTIVE_PROFILE="stepsolve-hotspot"
assert_eq "home out of range, already hotspot" "" "$(run_scenario '')"

# Scenario 3: home SSID not in range, currently on a stale/disconnected profile -> switch to hotspot
export FAKE_VISIBLE_SSIDS=""
export FAKE_ACTIVE_PROFILE="--"
assert_eq "home out of range, disconnected" "stepsolve-hotspot" "$(run_scenario '')"

# Scenario 4: home SSID in range, already connected to it -> no-op
export FAKE_VISIBLE_SSIDS="MyHomeWifi"
export FAKE_ACTIVE_PROFILE="stepsolve-home"
assert_eq "already connected to home" "" "$(run_scenario '')"

# Scenario 5: home + phone hotspot both in range, home has higher priority -> picks home
export FAKE_KNOWN_PROFILES="stepsolve-home=MyHomeWifi;stepsolve-phone=PhonePersonalHotspot"
export FAKE_PROFILE_PRIORITIES="stepsolve-home=10;stepsolve-phone=5"
export FAKE_VISIBLE_SSIDS="PhonePersonalHotspot\nMyHomeWifi"
export FAKE_ACTIVE_PROFILE="stepsolve-hotspot"
assert_eq "home outranks phone hotspot" "stepsolve-home" "$(run_scenario '')"

# Scenario 6: only phone hotspot in range (home out of range) -> falls back to phone hotspot
export FAKE_VISIBLE_SSIDS="PhonePersonalHotspot"
export FAKE_ACTIVE_PROFILE="stepsolve-hotspot"
assert_eq "falls back to phone hotspot" "stepsolve-phone" "$(run_scenario '')"

# Scenario 7: force-hotspot mode overrides even though a known network is in range
export FAKE_KNOWN_PROFILES="stepsolve-home=MyHomeWifi"
export FAKE_PROFILE_PRIORITIES=""
export FAKE_VISIBLE_SSIDS="MyHomeWifi"
export FAKE_ACTIVE_PROFILE="stepsolve-home"
assert_eq "force-hotspot overrides known network" "stepsolve-hotspot" "$(run_scenario 'force-hotspot')"

# Scenario 8: force-client mode with no known network in range -> no hotspot fallback
export FAKE_VISIBLE_SSIDS=""
export FAKE_ACTIVE_PROFILE="--"
assert_eq "force-client never falls back to hotspot" "" "$(run_scenario 'force-client')"

exit $fail
