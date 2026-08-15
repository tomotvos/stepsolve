# StepSolve — Raspberry Pi Setup Guide

There are two installation paths depending on whether you have a Mac available.

- **[Developer / Mac path](#developer-mac-path)** — builds the binary locally and deploys via `rsync`. Recommended for active development.
- **[Shell-only path](#shell-only-path)** — downloads a pre-built release tarball from GitHub. No Mac or .NET SDK required.

---

## Developer / Mac Path

### Prerequisites

- Raspberry Pi 4 (2 GB+ RAM recommended) with a compatible camera module
- A Mac with .NET 10 SDK installed (`brew install dotnet`)
- A microSD card (16 GB+)

---

## 1. Flash the SD Card

Use **Raspberry Pi Imager** (https://www.raspberrypi.com/software/).

- **OS**: Raspberry Pi OS Lite, 64-bit (Bookworm)
- In **Advanced Options** (gear icon):
  - Hostname: `stepsolve`
  - Username: `pi`, set a password or paste your SSH public key
  - Configure WiFi (your home network)
  - Enable SSH

Flash, insert into Pi, power on. Wait ~60 seconds for first boot.

---

## 2. First Install (from your Mac)

This single command builds the binary, rsyncs it to the Pi, and runs the installer:

```bash
cd stepsolve
PI_HOST=pi@rpistepsolve.local bash scripts/deploy.sh --install
```

The installer will:
- Create the `stepsolve` system user
- Set up directory structure under `/var/lib/stepsolve/`
- Install and enable the systemd service (starts on every boot)
- Set up the Tetra3 Python venv
- Configure mDNS via Avahi
- Optionally install `astrometry.net`

When it finishes, the dashboard is at **http://stepsolve.local:5001**.

---

## 3. Configure Solver Paths

After installation, the installer prints the Tetra3 database path. Open the dashboard at **http://stepsolve.local:5001**, go to **Settings → Solver**, and set:

- **Backend**: `tetra3`
- **FOV Estimate**: your camera's diagonal field of view in degrees (e.g. `34.3` for a Pi Camera Module 3 with the standard lens)
- **Tetra3 IndexPath**: the path printed by the installer, e.g.:
  `/var/lib/stepsolve/solvers/.venv/lib/python3.11/site-packages/tetra3/data/default_database`

> The exact path varies with the Python version on your Pi. Always use the path printed during install rather than guessing.

Save, then switch Mode to **Solve** from the dashboard header.

---

## 4. Astrometry.net Index Files (optional)

If using the `astrometry` backend, download index files matching your FOV. Place them in `/var/lib/stepsolve/indexes/astrometry/`:

```bash
ssh pi@rpistepsolve.local
mkdir -p /var/lib/stepsolve/indexes/astrometry
cd /var/lib/stepsolve/indexes/astrometry

# 4100-series: wide to medium fields (up to ~60°)
# Pick the series that matches your FOV — narrower FOV = higher series number
wget http://data.astrometry.net/4100/index-4107.fits  # ~60°
wget http://data.astrometry.net/4100/index-4108.fits
wget http://data.astrometry.net/4100/index-4109.fits
wget http://data.astrometry.net/4100/index-4110.fits  # ~30°
```

Then update `Solver:Astrometry:IndexPath` to `/var/lib/stepsolve/indexes/astrometry`.

---

## 5. Camera Permissions

The installer adds the `stepsolve` user to the `video` group, which `rpicam-still` requires. If you see permission errors in the logs, verify:

```bash
groups stepsolve   # should include 'video'
```

A reboot after install ensures group membership takes effect.

---

## 6. Iterative Deploys (during development)

After the first install, subsequent deploys (no reinstall needed):

```bash
bash scripts/deploy.sh
```

This builds, rsyncs, and restarts the service in ~30 seconds.

---

## 7. Wi-Fi Hotspot (Field Use)

The installer sets up automatic switching between known Wi-Fi networks and a
self-hosted hotspot, so `stepsolve.local` is always reachable — at home, at
a site with only your phone available, or fully in the field with neither.

A systemd timer (`stepsolve-hotspot-switch.timer`) checks every 30 seconds
whether any Wi-Fi network the Pi already knows about is in range, and
activates the highest-priority one it finds — falling back to the Pi's own
hotspot only if none are. The recommended three-tier setup, in preference
order:

1. **Home Wi-Fi** (priority `10`) — any Wi-Fi network already saved on the
   Pi (e.g. the home network set up via Raspberry Pi Imager when you
   flashed the SD card) is automatically bumped to priority `10` by the
   installer, so it outranks the phone fallback below. Used whenever
   you're close enough to home to reach it (backyard, driveway).
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

**For the Pi to actually detect it**, confirmed by real testing:

- Your phone must not be connected to its own Wi-Fi network at the time —
  when the phone is already on Wi-Fi, its Personal/Mobile Hotspot may not
  broadcast reliably even if the toggle shows "on." Make sure it's on
  cellular only.
- Enable "Maximize Compatibility" (iPhone: Settings → Personal Hotspot) or
  the equivalent 2.4GHz-only setting (Android, varies by manufacturer).
  Phones often default their hotspot to 5GHz when the connecting device
  supports it, but this Pi's own hotspot — and critically, its ability to
  scan for other networks *while* broadcasting one — is 2.4GHz-only
  (`802-11-wireless.band bg` in `setup-hotspot.sh`). A 5GHz-only phone
  hotspot may never be seen while the Pi is in AP mode.
- Use a plain, ASCII-only name for the phone's hotspot (on iPhone, rename
  the device itself under Settings → General → About → Name, since the
  default hotspot name derives from it) and register that *exact* name as
  `PHONE_HOTSPOT_SSID`. Default iPhone hotspot names use a Unicode "smart
  quote" apostrophe (e.g. `Tom's iPhone` with `’`, not a plain `'`), which
  can get mangled passing through SSH → shell → env var → `nmcli` →
  NetworkManager — a silent match failure that's easy to misdiagnose as a
  Wi-Fi problem rather than a naming one. Verify what's actually registered
  with:
  ```bash
  nmcli -g 802-11-wireless.ssid connection show stepsolve-phone
  ```
  and compare it against what the phone actually broadcasts.

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

### Testing tips: simulating network changes safely

A few non-obvious gotchas from real testing, worth knowing before you dig in:

- **`force-hotspot` persists across reboots and never self-recovers.** It's
  a plain file at `/etc/stepsolve/hotspot-mode`, re-enforced by the timer
  on every boot and every 30s tick — that's the entire point of "force":
  it deliberately ignores whatever networks are in range until you change
  the mode back. Don't reboot while it's set unless you mean to, or SSH
  over your home network becomes unreliable (the timer will yank `wlan0`
  onto the Pi's own hotspot within ~20-30s of boot, mid-session). If this
  happens: `ssh pi@rpistepsolve.local "sudo stepsolve-hotspot auto"` (a single
  non-interactive command survives a short/flaky connection window better
  than an interactive login), or connect to the `StepSolve` hotspot
  directly (default gateway `10.42.0.1`) and reset it from there with no
  time pressure.

- **`nmcli connection down <profile>` does not simulate "out of range."**
  It only disconnects the Pi from that network — the router keeps
  broadcasting, so the very next scan finds it again and the timer just
  reconnects, with no visible effect. To genuinely test the
  no-known-network-in-range fallback path without physically moving the
  Pi, temporarily point the profile at a nonexistent SSID instead:
  ```bash
  nmcli -g 802-11-wireless.ssid connection show preconfigured   # note the real one first
  sudo nmcli connection modify preconfigured 802-11-wireless.ssid "NOT-REALLY-IN-RANGE-TEST"
  ```
  Within ~30s the timer should fall back to `stepsolve-hotspot` (this will
  drop an active SSH session over that network — expected, and confirms
  the switch happened). Restore the real SSID afterward to test recovery
  back to it, unattended:
  ```bash
  sudo nmcli connection modify preconfigured 802-11-wireless.ssid "<real SSID>"
  ```
  Don't run any `stepsolve-hotspot` command after restoring it — the point
  is confirming the 30s timer notices and switches back on its own.

- **Priority only matters among networks currently visible in a scan** —
  it is not a fallback chain that tries each configured network in turn
  regardless of whether it's actually broadcasting. If a higher-priority
  network you expected to lose isn't actually in range at that moment
  (phone hotspot off or not detectable, etc.), the next-priority network —
  or the Pi's own hotspot if nothing matches — wins immediately, on that
  same tick.

- **If this Pi previously ran skysolve-next**, check for a leftover
  competing hotspot manager before testing:
  ```bash
  systemctl list-units --all | grep -iE "access|skysolve"
  ```
  `AccessPopup.timer`/`AccessPopup.service` (from RaspberryConnect.com,
  often installed alongside old skysolve-next setups) does its own
  NetworkManager-based hotspot switching and will fight
  `stepsolve-hotspot-switch.timer` for control of `wlan0` if left enabled:
  ```bash
  sudo systemctl disable --now AccessPopup.timer AccessPopup.service
  ```

---

## 8. Configuration Reference

Key settings that differ from Mac development defaults:

| Setting | Mac default | Pi value |
|---------|-------------|----------|
| `StepSolve:Mode` | `idle` | `solve` (set via dashboard) |
| `Solver:Backend` | `astrometry` | `tetra3` (recommended) |
| `Solver:FovEstimateDeg` | `0.0` | Your camera FOV (e.g. `34.3`) |
| `Solver:Tetra3:IndexPath` | local dev venv | `/var/lib/stepsolve/solvers/.venv/lib/pythonX.Y/site-packages/tetra3/data/default_database` |
| `Solver:Astrometry:IndexPath` | local | `/var/lib/stepsolve/indexes/astrometry` |
| `Solver:Astrometry:SolveFieldPath` | `/opt/homebrew/bin/solve-field` | `solve-field` (on PATH after apt install) |

---

---

## Shell-Only Path

Use this if you don't have a Mac or just want to install a pre-built release on a Pi with SSH access.

### Prerequisites

- Raspberry Pi 4 with a camera module
- Raspberry Pi OS Lite, 64-bit (Bookworm) — see [Section 1](#1-flash-the-sd-card) for flash instructions
- SSH access to the Pi

### 1. Download and extract the latest release

SSH into the Pi, then:

```bash
# Find the latest release URL at https://github.com/tomotvos/stepsolve/releases
# Replace <version> with the actual version tag, e.g. v1.0.0
VERSION=v1.0.0
wget -O /tmp/stepsolve.tar.gz \
    "https://github.com/tomotvos/stepsolve/releases/download/$VERSION/stepsolve-${VERSION}-arm64.tar.gz"

sudo mkdir -p /usr/local/lib/stepsolve
sudo tar -xzf /tmp/stepsolve.tar.gz -C /usr/local/lib/stepsolve
rm /tmp/stepsolve.tar.gz
```

### 2. Run the installer

```bash
sudo bash /usr/local/lib/stepsolve/deploy/install.sh
```

The installer will set up the `stepsolve` user, systemd service, mDNS, and the Tetra3 solver venv. It will print the Tetra3 database path at the end.

### 3. Configure and verify

Follow [Section 3](#3-configure-solver-paths) (use the dashboard at `http://stepsolve.local:5001`).

### Updating

Open the dashboard, go to **Software Update**, and click **Update now** if a newer version is available. No SSH needed.

---

## 9. Troubleshooting

```bash
# View live logs
journalctl -fu stepsolve

# Check service status
systemctl status stepsolve

# Restart manually
sudo systemctl restart stepsolve

# Test camera (run as pi user)
rpicam-still -o /tmp/test.jpg --shutter 1000000 --gain 8

# Test Tetra3 venv
sudo -u stepsolve /var/lib/stepsolve/solvers/.venv/bin/python -c "import tetra3; print('ok')"
```
