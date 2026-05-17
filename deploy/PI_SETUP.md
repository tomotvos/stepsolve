# StepSolve — Raspberry Pi Setup Guide

## Prerequisites

- Raspberry Pi 4 (2 GB+ RAM recommended) with a compatible camera module
- A Mac to build and deploy from
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

After installation, the installer prints the Tetra3 database path. Copy it into `/var/lib/stepsolve/appsettings.runtime.json` (or edit via the dashboard Settings panel):

```json
{
  "Solver": {
    "Backend": "tetra3",
    "FovEstimateDeg": 34.3,
    "Tetra3": {
      "IndexPath": "/var/lib/stepsolve/solvers/.venv/lib/python3.11/site-packages/tetra3/data/default_database"
    }
  }
}
```

> The exact path varies with the Python version. Use the path printed during install.

### FOV Estimate

Set `Solver:FovEstimateDeg` to your camera's field of view. For a Raspberry Pi Camera Module 3 with the standard lens: ~34°. Tetra3 solves faster and more reliably with an accurate FOV.

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
