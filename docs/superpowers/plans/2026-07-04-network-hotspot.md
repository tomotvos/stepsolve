# Network Hotspot (Issue #3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the Pi the ability to automatically switch its Wi-Fi (`wlan0`) between client mode (joins a known network) and hotspot/AP mode (creates its own network for field use), so `stepsolve.local` and both StepSolve ports are always reachable — plus a manual override for testing the hotspot at home, and support for a ranked list of known networks (home, a phone hotspot, etc.) rather than just one.

**Architecture:** Raspberry Pi OS Bookworm manages networking through NetworkManager by default (no `dhcpcd`/`hostapd`/`dnsmasq` package stack, unlike the `skysolve_legacy/Autohotspot/` scripts this replaces). We create one new NetworkManager connection profile, `stepsolve-hotspot`, using NM's built-in `ipv4.method shared` AP mode (NM runs its own internal DHCP/NAT — no extra packages). A systemd timer runs a switch script every 30 seconds that either (a) obeys a manual override mode, or (b) in `auto` mode, scans for all *other* saved Wi-Fi profiles (the home network from Raspberry Pi Imager, and any others you add — e.g. a phone hotspot), picks the highest-priority one currently in range using NetworkManager's own `connection.autoconnect-priority` field, and activates it — falling back to `stepsolve-hotspot` if none are in range. A small control script (`hotspot-ctl.sh`, installed as `stepsolve-hotspot`) lets you force hotspot or client mode regardless of what's in range, for testing at home.

**Tech Stack:** Bash, `nmcli` (NetworkManager CLI), systemd (`.service` + `.timer` units), the existing `deploy/install.sh` installer pattern.

## Global Constraints

- Target OS is Raspberry Pi OS Lite 64-bit **Bookworm**, which uses NetworkManager, not `dhcpcd`/`wpa_supplicant`/`hostapd`/`dnsmasq`. Do not install or configure those packages.
- No GPIO physical override switch — SSID-in-range detection (plus the manual mode-file override below) only, per issue #3's description (confirmed with user; a GPIO switch can be a separate future issue).
- All new shell scripts use `#!/usr/bin/env bash` and `set -euo pipefail`, except `hotspot-switch.sh`'s own body which uses `set -uo pipefail` (it must tolerate individual `nmcli` failures without aborting the whole decision loop).
- Follow the existing `deploy/install.sh` idempotent-install pattern (checks before installing, safe to re-run).
- Default hotspot SSID `StepSolve`, default password `stepsolve1234`, both overridable via `HOTSPOT_SSID` / `HOTSPOT_PASSWORD` env vars at install time.
- Manual override mode lives in `/etc/stepsolve/hotspot-mode` (`auto` | `force-hotspot` | `force-client`, default `auto`), controlled via the installed `stepsolve-hotspot` command.
- Known-network preference ordering uses NetworkManager's own `connection.autoconnect-priority` field on each saved Wi-Fi profile (higher wins) — no separate config file for this, so `nmcli connection modify <profile> connection.autoconnect-priority N` is the one and only way to rank networks.

---

## File Structure

```
stepsolve/deploy/hotspot/
  setup-hotspot.sh                    # idempotent: creates/updates the stepsolve-hotspot NM AP profile,
                                       # and seeds /etc/stepsolve/hotspot-mode with "auto" if missing
  hotspot-switch.sh                   # runs every 30s via timer; decides AP vs client and switches,
                                       # honoring the mode file override
  hotspot-ctl.sh                      # manual control: auto|force-hotspot|force-client|status
  stepsolve-hotspot-switch.service    # systemd oneshot unit that runs hotspot-switch.sh
  stepsolve-hotspot-switch.timer      # systemd timer (20s boot delay, 30s interval)
  tests/
    nmcli                              # stub nmcli binary used only by the test harness
    test_hotspot_switch.sh            # scenario tests for hotspot-switch.sh's decision logic
    test_hotspot_ctl.sh               # tests for hotspot-ctl.sh's argument handling / mode writing

stepsolve/deploy/install.sh            # modified: add hotspot setup + timer + control script install
stepsolve/deploy/PI_SETUP.md           # modified: add "Wi-Fi Hotspot (Field Use)" section
```

`hotspot-switch.sh` keeps its `nmcli`-calling logic behind small functions and a `main()` that only runs when the script is executed directly (`[[ "${BASH_SOURCE[0]}" == "$0" ]]`), so `tests/test_hotspot_switch.sh` can `source` it and call `main` with a fake `nmcli` on `PATH` and a temp file for the mode override — this makes the switching *logic*, including priority ordering and forced modes, fully testable on macOS without a real Pi or NetworkManager. `hotspot-ctl.sh` is tested the same way with a stubbed-out `hotspot-switch.sh`. `setup-hotspot.sh` and the systemd units are not unit-testable this way (they mutate real NM/systemd state); they get a syntax check here, and real verification against the Pi is issue #16, done after this plan lands.

---

### Task 1: Hotspot switch script + decision-logic tests

**Files:**
- Create: `deploy/hotspot/hotspot-switch.sh`
- Create: `deploy/hotspot/tests/nmcli`
- Create: `deploy/hotspot/tests/test_hotspot_switch.sh`

**Interfaces:**
- Produces: `deploy/hotspot/hotspot-switch.sh` — a standalone script, `ExecStart` target for `stepsolve-hotspot-switch.service` (Task 4) and invoked by `hotspot-ctl.sh` (Task 3). Reads optional env vars `STEPSOLVE_WIFI_DEV` (default `wlan0`) and `STEPSOLVE_HOTSPOT_MODE_FILE` (default `/etc/stepsolve/hotspot-mode`). Hardcodes hotspot profile name `stepsolve-hotspot` (must match the name `setup-hotspot.sh` in Task 2 creates).

- [ ] **Step 1: Write the fake `nmcli` test stub**

Create `deploy/hotspot/tests/nmcli`:

```bash
#!/usr/bin/env bash
# Stub nmcli for testing hotspot-switch.sh / hotspot-ctl.sh in isolation.
# Controlled via env vars set by the test:
#   FAKE_KNOWN_PROFILES     "name1=ssid1;name2=ssid2"  (client profiles nmcli knows about)
#   FAKE_PROFILE_PRIORITIES "name1=10;name2=5"         (connection.autoconnect-priority per profile, default 0)
#   FAKE_VISIBLE_SSIDS      "ssid-a\nssid-b"           (SSIDs the scan sees, one per line)
#   FAKE_ACTIVE_PROFILE     "some-profile-name"        (currently active connection on the device)
#   NMCLI_UP_LOG            path to append each "connection up <name>" call to

args=("$@")

if [[ "${args[0]}" == "-t" && "${args[2]}" == "NAME,TYPE" && "${args[3]}" == "connection" && "${args[4]}" == "show" ]]; then
    IFS=';' read -ra pairs <<< "$FAKE_KNOWN_PROFILES"
    for pair in "${pairs[@]}"; do
        [[ -z "$pair" ]] && continue
        name="${pair%%=*}"
        echo "${name}:802-11-wireless"
    done
    echo "stepsolve-hotspot:802-11-wireless"
    exit 0
fi

if [[ "${args[0]}" == "-g" && "${args[1]}" == "802-11-wireless.ssid" && "${args[2]}" == "connection" && "${args[3]}" == "show" ]]; then
    profile="${args[4]}"
    IFS=';' read -ra pairs <<< "$FAKE_KNOWN_PROFILES"
    for pair in "${pairs[@]}"; do
        [[ -z "$pair" ]] && continue
        name="${pair%%=*}"
        ssid="${pair#*=}"
        if [[ "$name" == "$profile" ]]; then
            echo "$ssid"
            exit 0
        fi
    done
    exit 1
fi

if [[ "${args[0]}" == "-g" && "${args[1]}" == "connection.autoconnect-priority" && "${args[2]}" == "connection" && "${args[3]}" == "show" ]]; then
    profile="${args[4]}"
    IFS=';' read -ra pairs <<< "$FAKE_PROFILE_PRIORITIES"
    for pair in "${pairs[@]}"; do
        [[ -z "$pair" ]] && continue
        name="${pair%%=*}"
        prio="${pair#*=}"
        if [[ "$name" == "$profile" ]]; then
            echo "$prio"
            exit 0
        fi
    done
    echo "0"
    exit 0
fi

if [[ "${args[0]}" == "-t" && "${args[2]}" == "SSID" && "${args[3]}" == "dev" && "${args[4]}" == "wifi" ]]; then
    printf '%b\n' "$FAKE_VISIBLE_SSIDS"
    exit 0
fi

if [[ "${args[0]}" == "-t" && "${args[2]}" == "GENERAL.CONNECTION" && "${args[3]}" == "dev" && "${args[4]}" == "show" ]]; then
    echo "GENERAL.CONNECTION:${FAKE_ACTIVE_PROFILE:---}"
    exit 0
fi

if [[ "${args[0]}" == "connection" && "${args[1]}" == "up" ]]; then
    echo "${args[2]}" >> "$NMCLI_UP_LOG"
    exit 0
fi

echo "nmcli stub: unhandled args: ${args[*]}" >&2
exit 1
```

```bash
chmod +x deploy/hotspot/tests/nmcli
```

- [ ] **Step 2: Write the test scenarios**

Create `deploy/hotspot/tests/test_hotspot_switch.sh`:

```bash
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
FAKE_KNOWN_PROFILES="stepsolve-home=MyHomeWifi"
FAKE_PROFILE_PRIORITIES=""
FAKE_VISIBLE_SSIDS="MyHomeWifi\nSomeNeighborWifi"
FAKE_ACTIVE_PROFILE="stepsolve-hotspot"
assert_eq "home in range, hotspot active" "stepsolve-home" "$(run_scenario '')"

# Scenario 2: home SSID not in range, hotspot already active -> no-op (already correct)
FAKE_VISIBLE_SSIDS="SomeNeighborWifi"
FAKE_ACTIVE_PROFILE="stepsolve-hotspot"
assert_eq "home out of range, already hotspot" "" "$(run_scenario '')"

# Scenario 3: home SSID not in range, currently on a stale/disconnected profile -> switch to hotspot
FAKE_VISIBLE_SSIDS=""
FAKE_ACTIVE_PROFILE="--"
assert_eq "home out of range, disconnected" "stepsolve-hotspot" "$(run_scenario '')"

# Scenario 4: home SSID in range, already connected to it -> no-op
FAKE_VISIBLE_SSIDS="MyHomeWifi"
FAKE_ACTIVE_PROFILE="stepsolve-home"
assert_eq "already connected to home" "" "$(run_scenario '')"

# Scenario 5: home + phone hotspot both in range, home has higher priority -> picks home
FAKE_KNOWN_PROFILES="stepsolve-home=MyHomeWifi;stepsolve-phone=PhonePersonalHotspot"
FAKE_PROFILE_PRIORITIES="stepsolve-home=10;stepsolve-phone=5"
FAKE_VISIBLE_SSIDS="PhonePersonalHotspot\nMyHomeWifi"
FAKE_ACTIVE_PROFILE="stepsolve-hotspot"
assert_eq "home outranks phone hotspot" "stepsolve-home" "$(run_scenario '')"

# Scenario 6: only phone hotspot in range (home out of range) -> falls back to phone hotspot
FAKE_VISIBLE_SSIDS="PhonePersonalHotspot"
FAKE_ACTIVE_PROFILE="stepsolve-hotspot"
assert_eq "falls back to phone hotspot" "stepsolve-phone" "$(run_scenario '')"

# Scenario 7: force-hotspot mode overrides even though a known network is in range
FAKE_KNOWN_PROFILES="stepsolve-home=MyHomeWifi"
FAKE_PROFILE_PRIORITIES=""
FAKE_VISIBLE_SSIDS="MyHomeWifi"
FAKE_ACTIVE_PROFILE="stepsolve-home"
assert_eq "force-hotspot overrides known network" "stepsolve-hotspot" "$(run_scenario 'force-hotspot')"

# Scenario 8: force-client mode with no known network in range -> no hotspot fallback
FAKE_VISIBLE_SSIDS=""
FAKE_ACTIVE_PROFILE="--"
assert_eq "force-client never falls back to hotspot" "" "$(run_scenario 'force-client')"

exit $fail
```

```bash
chmod +x deploy/hotspot/tests/test_hotspot_switch.sh
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `bash deploy/hotspot/tests/test_hotspot_switch.sh`
Expected: FAIL — `deploy/hotspot/hotspot-switch.sh: No such file or directory` (script doesn't exist yet)

- [ ] **Step 4: Implement `hotspot-switch.sh`**

Create `deploy/hotspot/hotspot-switch.sh`:

```bash
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
```

```bash
chmod +x deploy/hotspot/hotspot-switch.sh
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `bash deploy/hotspot/tests/test_hotspot_switch.sh`
Expected:
```
PASS: home in range, hotspot active
PASS: home out of range, already hotspot
PASS: home out of range, disconnected
PASS: already connected to home
PASS: home outranks phone hotspot
PASS: falls back to phone hotspot
PASS: force-hotspot overrides known network
PASS: force-client never falls back to hotspot
```
(exit code 0)

- [ ] **Step 6: Syntax-check with bash and shellcheck if available**

Run: `bash -n deploy/hotspot/hotspot-switch.sh && bash -n deploy/hotspot/tests/test_hotspot_switch.sh && bash -n deploy/hotspot/tests/nmcli`
Expected: no output, exit code 0.

Run (only if `shellcheck` is installed — skip if not, it's not a repo dependency): `shellcheck deploy/hotspot/hotspot-switch.sh`
Expected: no errors (warnings about the `logger` fallback pattern are acceptable).

- [ ] **Step 7: Commit**

```bash
git add deploy/hotspot/hotspot-switch.sh deploy/hotspot/tests/nmcli deploy/hotspot/tests/test_hotspot_switch.sh
git commit -m "feat: add Wi-Fi hotspot/client auto-switch script"
```

---

### Task 2: Hotspot NetworkManager profile setup script

**Files:**
- Create: `deploy/hotspot/setup-hotspot.sh`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: the `stepsolve-hotspot` NetworkManager AP profile that `hotspot-switch.sh` (Task 1) activates by name; the optional `stepsolve-phone` NetworkManager client profile (a ranked fallback network, e.g. a phone's hotspot) when `PHONE_HOTSPOT_SSID` is provided; and the default `/etc/stepsolve/hotspot-mode` file (`auto`) that `hotspot-switch.sh` and `hotspot-ctl.sh` (Task 3) read/write. Invoked by `install.sh` (Task 5) with `HOTSPOT_SSID` / `HOTSPOT_PASSWORD` / `PHONE_HOTSPOT_SSID` / `PHONE_HOTSPOT_PASSWORD` / `PHONE_HOTSPOT_PRIORITY` env vars.

- [ ] **Step 1: Implement `setup-hotspot.sh`**

Create `deploy/hotspot/setup-hotspot.sh`:

```bash
#!/usr/bin/env bash
# Idempotently creates or updates the stepsolve-hotspot NetworkManager AP profile,
# optionally a phone-hotspot fallback client profile, and seeds the default
# hotspot mode file if it doesn't exist.
# Override defaults with HOTSPOT_SSID / HOTSPOT_PASSWORD env vars.
# Set PHONE_HOTSPOT_SSID / PHONE_HOTSPOT_PASSWORD (and optionally
# PHONE_HOTSPOT_PRIORITY, default 5) to also register a fallback network,
# e.g. your phone's personal hotspot, ranked between home Wi-Fi and the Pi's
# own hotspot.
set -euo pipefail

WIFI_DEV="${STEPSOLVE_WIFI_DEV:-wlan0}"
PROFILE="stepsolve-hotspot"
SSID="${HOTSPOT_SSID:-StepSolve}"
PASSWORD="${HOTSPOT_PASSWORD:-stepsolve1234}"
MODE_FILE="${STEPSOLVE_HOTSPOT_MODE_FILE:-/etc/stepsolve/hotspot-mode}"

if [[ ${#PASSWORD} -lt 8 ]]; then
    echo "HOTSPOT_PASSWORD must be at least 8 characters" >&2
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
echo "    Hotspot password: $PASSWORD"

if [[ -n "${PHONE_HOTSPOT_SSID:-}" ]]; then
    PHONE_PROFILE="stepsolve-phone"
    PHONE_PASSWORD="${PHONE_HOTSPOT_PASSWORD:-}"
    PHONE_PRIORITY="${PHONE_HOTSPOT_PRIORITY:-5}"

    if [[ ${#PHONE_PASSWORD} -lt 8 ]]; then
        echo "PHONE_HOTSPOT_PASSWORD must be at least 8 characters when PHONE_HOTSPOT_SSID is set" >&2
        exit 1
    fi

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

echo "==> Seeding hotspot mode file"
mkdir -p "$(dirname "$MODE_FILE")"
if [[ ! -f "$MODE_FILE" ]]; then
    echo "auto" > "$MODE_FILE"
fi
echo "    Mode file: $MODE_FILE (current: $(cat "$MODE_FILE"))"
```

```bash
chmod +x deploy/hotspot/setup-hotspot.sh
```

- [ ] **Step 2: Syntax-check**

Run: `bash -n deploy/hotspot/setup-hotspot.sh`
Expected: no output, exit code 0.

Run (only if `shellcheck` is installed): `shellcheck deploy/hotspot/setup-hotspot.sh`
Expected: no errors.

- [ ] **Step 3: Commit**

```bash
git add deploy/hotspot/setup-hotspot.sh
git commit -m "feat: add idempotent NetworkManager hotspot profile setup script"
```

---

### Task 3: Manual override control script (`hotspot-ctl.sh`)

**Files:**
- Create: `deploy/hotspot/hotspot-ctl.sh`
- Create: `deploy/hotspot/tests/test_hotspot_ctl.sh`

**Interfaces:**
- Consumes: `deploy/hotspot/hotspot-switch.sh` (Task 1) via the `STEPSOLVE_HOTSPOT_SWITCH_SCRIPT` env var (default `/usr/local/lib/stepsolve/deploy/hotspot/hotspot-switch.sh` — a hardcoded absolute path, *not* derived from `hotspot-ctl.sh`'s own location, because `install.sh` (Task 5) copies this script to `/usr/local/bin/stepsolve-hotspot`, a different directory than where `hotspot-switch.sh` lives).
- Produces: the installed `stepsolve-hotspot` command (`sudo stepsolve-hotspot force-hotspot|force-client|auto|status`) used for testing the hotspot at home.

- [ ] **Step 1: Write the test scenarios**

Create `deploy/hotspot/tests/test_hotspot_ctl.sh`:

```bash
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
```

```bash
chmod +x deploy/hotspot/tests/test_hotspot_ctl.sh
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `bash deploy/hotspot/tests/test_hotspot_ctl.sh`
Expected: FAIL — `deploy/hotspot/hotspot-ctl.sh: No such file or directory`

- [ ] **Step 3: Implement `hotspot-ctl.sh`**

Create `deploy/hotspot/hotspot-ctl.sh`:

```bash
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
```

```bash
chmod +x deploy/hotspot/hotspot-ctl.sh
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `bash deploy/hotspot/tests/test_hotspot_ctl.sh`
Expected:
```
PASS: force-hotspot writes mode file
PASS: force-hotspot invokes switch script
PASS: auto writes mode file
PASS: status reports mode
PASS: status reports active connection
PASS: bogus arg rejected
```
(exit code 0)

- [ ] **Step 5: Syntax-check**

Run: `bash -n deploy/hotspot/hotspot-ctl.sh && bash -n deploy/hotspot/tests/test_hotspot_ctl.sh`
Expected: no output, exit code 0.

- [ ] **Step 6: Commit**

```bash
git add deploy/hotspot/hotspot-ctl.sh deploy/hotspot/tests/test_hotspot_ctl.sh
git commit -m "feat: add manual hotspot/client override control script"
```

---

### Task 4: Systemd timer + service units

**Files:**
- Create: `deploy/hotspot/stepsolve-hotspot-switch.service`
- Create: `deploy/hotspot/stepsolve-hotspot-switch.timer`

**Interfaces:**
- Consumes: `deploy/hotspot/hotspot-switch.sh` (Task 1) — `ExecStart` path is the installed location `/usr/local/lib/stepsolve/deploy/hotspot/hotspot-switch.sh`, matching the existing pattern in `deploy/stepsolve.service`.

- [ ] **Step 1: Create the oneshot service unit**

Create `deploy/hotspot/stepsolve-hotspot-switch.service`:

```ini
[Unit]
Description=StepSolve Wi-Fi hotspot/client auto-switch
After=NetworkManager.service
Wants=NetworkManager.service

[Service]
Type=oneshot
ExecStart=/usr/local/lib/stepsolve/deploy/hotspot/hotspot-switch.sh
```

- [ ] **Step 2: Create the timer unit**

Create `deploy/hotspot/stepsolve-hotspot-switch.timer`:

```ini
[Unit]
Description=Run StepSolve Wi-Fi hotspot/client auto-switch periodically

[Timer]
OnBootSec=20s
OnUnitActiveSec=30s
Unit=stepsolve-hotspot-switch.service

[Install]
WantedBy=timers.target
```

- [ ] **Step 3: Note manual verification for the Pi**

`systemd-analyze` only exists on Linux with systemd — skip on macOS. Note for whoever runs issue #16's hardware verification: `systemd-analyze verify /usr/local/lib/stepsolve/deploy/hotspot/stepsolve-hotspot-switch.service` should produce no output.

- [ ] **Step 4: Commit**

```bash
git add deploy/hotspot/stepsolve-hotspot-switch.service deploy/hotspot/stepsolve-hotspot-switch.timer
git commit -m "feat: add systemd timer for Wi-Fi hotspot auto-switch"
```

---

### Task 5: Wire hotspot setup into `deploy/install.sh`

**Files:**
- Modify: `deploy/install.sh:87` (immediately after the existing Avahi section, before the "Optional: Astrometry.net" section)
- Modify: `deploy/install.sh:130-146` (final summary section)

**Interfaces:**
- Consumes: `deploy/hotspot/setup-hotspot.sh` (Task 2), `deploy/hotspot/hotspot-ctl.sh` (Task 3), `deploy/hotspot/stepsolve-hotspot-switch.service` + `.timer` (Task 4). Uses the same `$SCRIPT_DIR` variable install.sh already defines (`deploy/install.sh:16`).
- Produces: the `stepsolve-hotspot` command on `PATH` at `/usr/local/bin/stepsolve-hotspot`.

- [ ] **Step 1: Read the current Avahi section to anchor the insertion point**

The current file has this section ending at line 87 (`deploy/install.sh:77-87`):

```bash
# ── Avahi / mDNS ─────────────────────────────────────────────────────────────
if command -v avahi-daemon &>/dev/null; then
    echo "==> Installing Avahi service files"
    mkdir -p /etc/avahi/services
    cp "$SCRIPT_DIR/stepsolve-http.service"  /etc/avahi/services/
    cp "$SCRIPT_DIR/stepsolve-lx200.service" /etc/avahi/services/
    systemctl restart avahi-daemon || true
else
    echo "    avahi-daemon not found — mDNS (stepsolve.local) will not work."
    echo "    Install with: sudo apt-get install -y avahi-daemon"
fi
```

- [ ] **Step 2: Insert the Wi-Fi hotspot section right after it**

Use Edit to insert this new section immediately after the Avahi `fi` (before the blank line + `# ── Optional: Astrometry.net` comment):

```bash

# ── Wi-Fi Hotspot / Auto-switch ──────────────────────────────────────────────
if command -v nmcli &>/dev/null; then
    echo "==> Setting up Wi-Fi hotspot profile"
    HOTSPOT_SSID="${HOTSPOT_SSID:-StepSolve}" \
    HOTSPOT_PASSWORD="${HOTSPOT_PASSWORD:-stepsolve1234}" \
    PHONE_HOTSPOT_SSID="${PHONE_HOTSPOT_SSID:-}" \
    PHONE_HOTSPOT_PASSWORD="${PHONE_HOTSPOT_PASSWORD:-}" \
    PHONE_HOTSPOT_PRIORITY="${PHONE_HOTSPOT_PRIORITY:-5}" \
        bash "$SCRIPT_DIR/hotspot/setup-hotspot.sh"

    echo "==> Installing hotspot control command"
    install -m 755 "$SCRIPT_DIR/hotspot/hotspot-ctl.sh" /usr/local/bin/stepsolve-hotspot

    echo "==> Installing hotspot auto-switch timer"
    install -m 644 "$SCRIPT_DIR/hotspot/stepsolve-hotspot-switch.service" /etc/systemd/system/
    install -m 644 "$SCRIPT_DIR/hotspot/stepsolve-hotspot-switch.timer"   /etc/systemd/system/
    systemctl daemon-reload
    systemctl enable --now stepsolve-hotspot-switch.timer
else
    echo "    nmcli not found — Wi-Fi hotspot auto-switch will not be installed."
    echo "    Install with: sudo apt-get install -y network-manager"
fi
```

- [ ] **Step 3: Add hotspot info to the install summary**

In the final summary block (`deploy/install.sh:130-146`), after the line `echo "  LX200:      stepsolve.local:5002"`, insert:

```bash
echo "  Hotspot:    SSID '${HOTSPOT_SSID:-StepSolve}' when no known network is in range"
echo "              Force it for testing: sudo stepsolve-hotspot force-hotspot"
echo "              Back to auto:         sudo stepsolve-hotspot auto"
if [[ -n "${PHONE_HOTSPOT_SSID:-}" ]]; then
    echo "  Phone fallback: '${PHONE_HOTSPOT_SSID}' registered (priority ${PHONE_HOTSPOT_PRIORITY:-5})"
fi
```

- [ ] **Step 4: Verify script syntax**

Run: `bash -n deploy/install.sh`
Expected: no output, exit code 0.

- [ ] **Step 5: Commit**

```bash
git add deploy/install.sh
git commit -m "feat: install Wi-Fi hotspot auto-switch during Pi install"
```

---

### Task 6: Document the feature in PI_SETUP.md

**Files:**
- Modify: `deploy/PI_SETUP.md` (insert a new numbered section after the existing "6. Iterative Deploys" section, before "7. Configuration Reference"; renumber subsequent sections 7→8, 8→9)

**Interfaces:**
- None — documentation only.

- [ ] **Step 1: Insert the new section**

Insert after the `## 6. Iterative Deploys (during development)` section's closing `---`, and before `## 7. Configuration Reference` (which becomes `## 8`):

```markdown
## 7. Wi-Fi Hotspot (Field Use)

The installer sets up automatic switching between known Wi-Fi networks and a
self-hosted hotspot, so `stepsolve.local` is always reachable — at home, at
a site with only your phone available, or fully in the field with neither.

A systemd timer (`stepsolve-hotspot-switch.timer`) checks every 30 seconds
whether any Wi-Fi network the Pi already knows about is in range, and
activates the highest-priority one it finds — falling back to the Pi's own
hotspot only if none are. The recommended three-tier setup, in preference
order:

1. **Home Wi-Fi** (priority `10`) — set up automatically by Raspberry Pi
   Imager when you flashed the SD card. Used whenever you're close enough
   to home to reach it (backyard, driveway).
2. **Phone hotspot** (priority `5`) — a fallback network you register once
   (see below), e.g. your phone's Personal/Mobile Hotspot. Used at sites
   away from home where you'd rather just tap your phone's hotspot toggle
   than reconfigure anything.
3. **The Pi's own hotspot** (fixed lowest priority, only used when nothing
   else is in range) — for fully remote sites, or when your phone's
   hotspot feature is unavailable (carrier restrictions, battery saving,
   etc.). SSID `StepSolve`, password `stepsolve1234` by default.

Connect a phone or laptop to whichever network is active, then browse to
`http://stepsolve.local:5001` or connect SkySafari to `stepsolve.local:5002`.

### Registering a phone hotspot fallback

Provide `PHONE_HOTSPOT_SSID` and `PHONE_HOTSPOT_PASSWORD` (minimum 8
characters) when running the installer, and it's registered as tier 2
automatically, at priority `5` by default:

```bash
sudo env HOTSPOT_SSID=StepSolve HOTSPOT_PASSWORD=stepsolve1234 \
    PHONE_HOTSPOT_SSID="My iPhone" PHONE_HOTSPOT_PASSWORD=mypassword1 \
    bash deploy/install.sh
```

To add or change it after install without re-running the whole installer:

```bash
sudo env PHONE_HOTSPOT_SSID="My iPhone" PHONE_HOTSPOT_PASSWORD=mypassword1 \
    bash /usr/local/lib/stepsolve/deploy/hotspot/setup-hotspot.sh
```

Override the priority with `PHONE_HOTSPOT_PRIORITY` (default `5`) — keep it
below your home network's priority (`10`) so home is always preferred when
both are in range.

This only *registers* the network — you still turn your phone's hotspot on
when you actually need it in the field; the Pi will pick it up on its next
30-second scan.

**Any other known network** (a friend's house, a star party's shared
Wi-Fi) can be added the same way NetworkManager always adds networks, and
the auto-switch will pick it up automatically with no StepSolve-specific
config:

```bash
ssh pi@rpistepsolve.local
sudo nmcli device wifi connect "SomeOtherNetwork" password "its-password" ifname wlan0
sudo nmcli connection modify "SomeOtherNetwork" connection.autoconnect-priority 3
```

List your saved networks and their current priority with:

```bash
nmcli -f NAME,TYPE connection show
nmcli -g connection.autoconnect-priority connection show "<name>"
```

### Forcing hotspot mode for testing at home

Since auto mode only creates the Pi's own hotspot when no known network is
in range, you can't trigger it just by being at home. Use the installed
`stepsolve-hotspot` command to override:

```bash
sudo stepsolve-hotspot force-hotspot   # always hotspot, ignore known networks
sudo stepsolve-hotspot force-client    # only ever try known networks, never hotspot
sudo stepsolve-hotspot auto            # back to automatic switching (default)
sudo stepsolve-hotspot status          # show current mode + active connection
```

The override is re-applied every 30 seconds by the timer, so it holds even
if NetworkManager tries to reconnect a known network in the background —
expect at most one 30-second flicker back to auto behavior before it
self-corrects.

**Changing the Pi's own hotspot SSID/password:** set `HOTSPOT_SSID` and
`HOTSPOT_PASSWORD` (minimum 8 characters) before running the installer:

```bash
sudo env HOTSPOT_SSID=MyScope HOTSPOT_PASSWORD=mypassword1 bash deploy/install.sh
```

To change it after install without re-running the whole installer:

```bash
sudo env HOTSPOT_SSID=MyScope HOTSPOT_PASSWORD=mypassword1 \
    bash /usr/local/lib/stepsolve/deploy/hotspot/setup-hotspot.sh
```

> Use `sudo env VAR=value command`, not `VAR=value sudo command` — sudo
> resets the environment by default, so variables set *before* `sudo` are
> silently dropped. Placing them after `env` (which sudo runs as root)
> guarantees they reach the script.

**Troubleshooting:**

```bash
journalctl -t stepsolve-hotspot-switch -f
systemctl status stepsolve-hotspot-switch.timer
```

---
```

- [ ] **Step 2: Renumber the remaining sections**

Change `## 7. Configuration Reference` to `## 8. Configuration Reference`, and `## 8. Troubleshooting` to `## 9. Troubleshooting`.

- [ ] **Step 3: Commit**

```bash
git add deploy/PI_SETUP.md
git commit -m "docs: document Wi-Fi hotspot field-use setup"
```

---

### Task 7: Close out issue #15 and hand off issue #16 for hardware verification

**Files:** none — GitHub issue operations only.

- [ ] **Step 1: Comment on and close issue #15**

Issue #15 ("Avahi service files for HTTP and LX200") is already fully satisfied by existing `deploy/stepsolve-http.service`, `deploy/stepsolve-lx200.service`, and the Avahi install step already in `deploy/install.sh` — none of this plan's tasks touch it.

```bash
gh issue comment 15 --body "Already implemented: deploy/stepsolve-http.service and deploy/stepsolve-lx200.service exist and are installed by deploy/install.sh's Avahi section (restarts avahi-daemon after copying both files to /etc/avahi/services/). Closing as done."
gh issue close 15
```

- [ ] **Step 2: Comment on issue #14 referencing this work**

```bash
gh issue comment 14 --body "Implemented using NetworkManager (nmcli) rather than porting skysolve_legacy/Autohotspot's dhcpcd/hostapd/dnsmasq stack directly — Raspberry Pi OS Bookworm (the target OS per deploy/PI_SETUP.md) uses NetworkManager by default, and the old skysolve-next PRD had already flagged this as the intended direction. See deploy/hotspot/ (setup-hotspot.sh, hotspot-switch.sh, hotspot-ctl.sh, systemd timer) and deploy/install.sh's new Wi-Fi Hotspot section. No GPIO override — SSID-in-range detection plus a manual force-hotspot/force-client override (stepsolve-hotspot command) for testing, and priority-ranked fallback networks via NetworkManager's connection.autoconnect-priority."
gh issue close 14
```

- [ ] **Step 3: Leave issue #16 open with a note — it needs real hardware**

Issue #16's acceptance test ("phone connects to Pi's Wi-Fi AP → stepsolve.local resolves → dashboard loads on port 5001 → SkySafari finds StepSolve on port 5002 via mDNS") can now be tested at home using the force-hotspot override, but should also be confirmed once in the field. Comment to record what's ready to test:

```bash
gh issue comment 16 --body "Hotspot auto-switch is implemented (see #14). To verify: deploy to the Pi (bash scripts/deploy.sh --install), then run 'sudo stepsolve-hotspot force-hotspot' to trigger it on demand (works even at home), and confirm: (1) a StepSolve hotspot SSID appears, (2) connecting a phone to it resolves stepsolve.local, (3) the dashboard loads on port 5001, (4) SkySafari finds the LX200 service on port 5002 via mDNS. Also worth a real field test with 'sudo stepsolve-hotspot auto' (the default) away from any known network."
```

Do not close #16 — leave it open for the user to verify on hardware and close manually.

---

## Self-Review

**Spec coverage:**
- Issue #3 sub-item "Port autohotspot scripts... adapt for StepSolve service name" → Tasks 1, 2, 3, 4, 5 (NetworkManager-based equivalent, adapted to `stepsolve-*` naming, plus the manual override and ranked-fallback capabilities requested during review).
- Issue #3 sub-item "Avahi service files" → already done; Task 7 closes #15.
- Issue #3 sub-item "Verify AP mode" (#16) → Task 7 hands off with clear manual verification steps, now testable at home via `force-hotspot`; cannot be fully automated from this Mac session.
- Issue #3 sub-item "Verify client mode" (#17) → already closed prior to this plan (works via existing Avahi setup + NetworkManager's own client-mode handling); no task needed.
- User follow-up: "force a hotspot for testing" → Task 3 (`hotspot-ctl.sh` / `stepsolve-hotspot force-hotspot`).
- User follow-up: "connect to phone's hotspot as fallback, short ranked list of SSIDs" → Task 1's `pick_target()` priority ordering (scenarios 5–6) + Task 2's `PHONE_HOTSPOT_SSID`/`PHONE_HOTSPOT_PASSWORD`/`PHONE_HOTSPOT_PRIORITY` capture in `setup-hotspot.sh` + Task 6 docs on the recommended three-tier (home / phone / Pi-hotspot) priority convention.
- User follow-up: "encode phone-hotspot capture in the setup scripts" → Task 2's `setup-hotspot.sh` now creates/updates the `stepsolve-phone` client profile directly from env vars (no manual `ssh` + `nmcli` step required); Task 5 forwards those vars from `install.sh`.

**Placeholder scan:** none found — every script, unit file, and doc section above is complete, runnable content.

**Type/name consistency:** `HOTSPOT_PROFILE="stepsolve-hotspot"` in `hotspot-switch.sh` (Task 1) matches `PROFILE="stepsolve-hotspot"` in `setup-hotspot.sh` (Task 2) and the unit name `stepsolve-hotspot-switch.service`/`.timer` (Task 4). `MODE_FILE` default `/etc/stepsolve/hotspot-mode` is consistent across `hotspot-switch.sh` (Task 1), `setup-hotspot.sh` (Task 2, seeds it), and `hotspot-ctl.sh` (Task 3, writes it), all overridable via the same `STEPSOLVE_HOTSPOT_MODE_FILE` env var for test isolation. `HOTSPOT_SSID`/`HOTSPOT_PASSWORD` and `PHONE_HOTSPOT_SSID`/`PHONE_HOTSPOT_PASSWORD`/`PHONE_HOTSPOT_PRIORITY` env var names are consistent across `setup-hotspot.sh` (Task 2), `install.sh` (Task 5, forwards all five), and `PI_SETUP.md` (Task 6). The `stepsolve-phone` profile name Task 2 creates matches the renamed test fixtures in Task 1's scenarios 5–6. `hotspot-ctl.sh`'s `STEPSOLVE_HOTSPOT_SWITCH_SCRIPT` default path matches the `ExecStart` path in `stepsolve-hotspot-switch.service` and the `install -m 755 ... /usr/local/lib/stepsolve/deploy/hotspot/...` layout `install.sh` produces. All `sudo`-with-env-var examples in `PI_SETUP.md` use `sudo env VAR=value command` rather than `VAR=value sudo command`, since the latter is silently stripped by sudo's default `env_reset`.
