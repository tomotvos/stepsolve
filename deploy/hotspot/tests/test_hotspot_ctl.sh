#!/usr/bin/env bash
# Tests for hotspot-ctl.sh's argument handling and mode-file writing.
# Uses the fake nmcli from test_hotspot_switch.sh (for the `status` command)
# and a stubbed-out hotspot-switch.sh (so this test doesn't depend on real switching logic).
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export PATH="$SCRIPT_DIR:$PATH"
fail=0

TMP_MODE_FILE="$(mktemp)"
TMP_SWITCH_SCRIPT="$(mktemp)"
cat > "$TMP_SWITCH_SCRIPT" <<'EOF'
#!/usr/bin/env bash
echo "switch-invoked"
EOF
chmod +x "$TMP_SWITCH_SCRIPT"

export STEPSOLVE_HOTSPOT_MODE_FILE="$TMP_MODE_FILE"
export STEPSOLVE_HOTSPOT_SWITCH_SCRIPT="$TMP_SWITCH_SCRIPT"
export FAKE_ACTIVE_PROFILE="stepsolve-home"

assert_eq() {
    local scenario="$1" expected="$2" actual="$3"
    if [[ "$expected" != "$actual" ]]; then
        echo "FAIL: $scenario — expected '$expected', got '$actual'"
        fail=1
    else
        echo "PASS: $scenario"
    fi
}

out="$(bash "$SCRIPT_DIR/../hotspot-ctl.sh" force-hotspot)"
assert_eq "force-hotspot writes mode file" "force-hotspot" "$(cat "$TMP_MODE_FILE")"
assert_eq "force-hotspot invokes switch script" "switch-invoked" "$(echo "$out" | tail -n1)"

bash "$SCRIPT_DIR/../hotspot-ctl.sh" auto >/dev/null
assert_eq "auto writes mode file" "auto" "$(cat "$TMP_MODE_FILE")"

status_out="$(bash "$SCRIPT_DIR/../hotspot-ctl.sh" status)"
assert_eq "status reports mode" "Mode: auto" "$(echo "$status_out" | head -n1)"
assert_eq "status reports active connection" "Active connection on wlan0: stepsolve-home" "$(echo "$status_out" | tail -n1)"

if bash "$SCRIPT_DIR/../hotspot-ctl.sh" bogus 2>/dev/null; then
    echo "FAIL: bogus arg should exit non-zero"
    fail=1
else
    echo "PASS: bogus arg rejected"
fi

rm -f "$TMP_MODE_FILE" "$TMP_SWITCH_SCRIPT"
exit $fail
