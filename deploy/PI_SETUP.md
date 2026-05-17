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
PI_HOST=pi@stepsolve.local bash scripts/deploy.sh --install
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
ssh pi@stepsolve.local
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

## 7. Configuration Reference

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

## 8. Troubleshooting

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
