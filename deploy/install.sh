#!/usr/bin/env bash
# StepSolve Pi installer — idempotent, safe to re-run.
# Usage: sudo bash /usr/local/lib/stepsolve/deploy/install.sh
set -euo pipefail

# ── Checks ────────────────────────────────────────────────────────────────────
if [[ $EUID -ne 0 ]]; then
    echo "Run as root: sudo bash $0" >&2
    exit 1
fi
if [[ "$(uname -s)" != "Linux" ]]; then
    echo "This script is for Linux (Raspberry Pi OS) only." >&2
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"   # /usr/local/lib/stepsolve
DATA_DIR=/var/lib/stepsolve
UNIT_FILE=/etc/systemd/system/stepsolve.service
SUDOERS_FILE=/etc/sudoers.d/stepsolve

echo "======================================================"
echo "  StepSolve installer"
echo "  Install dir: $INSTALL_DIR"
echo "  Data dir:    $DATA_DIR"
echo "======================================================"

# ── System user ───────────────────────────────────────────────────────────────
if id stepsolve &>/dev/null; then
    echo "==> User 'stepsolve' already exists, skipping"
else
    echo "==> Creating system user 'stepsolve'"
    useradd -r -s /usr/sbin/nologin stepsolve
fi

# Add to video group for rpicam-still access
if groups stepsolve | grep -qw video; then
    echo "==> Already in video group"
else
    echo "==> Adding 'stepsolve' to video group"
    usermod -aG video stepsolve
fi

# ── Directory structure ───────────────────────────────────────────────────────
echo "==> Creating data directories"
mkdir -p "$DATA_DIR/images"
mkdir -p "$DATA_DIR/indexes/astrometry"
mkdir -p "$DATA_DIR/indexes/tetra3-default"
chown -R stepsolve:stepsolve "$DATA_DIR"

echo "==> Setting ownership of install directory"
chown -R stepsolve:stepsolve "$INSTALL_DIR"

# ── Systemd unit ──────────────────────────────────────────────────────────────
echo "==> Installing systemd unit"
# Rewrite ExecStart to point at actual binary in install dir
sed "s|ExecStart=.*|ExecStart=$INSTALL_DIR/stepsolve|" \
    "$SCRIPT_DIR/stepsolve.service" > "$UNIT_FILE"
# Also set WorkingDirectory to data dir
sed -i "s|WorkingDirectory=.*|WorkingDirectory=$DATA_DIR|" "$UNIT_FILE"

systemctl daemon-reload
systemctl enable stepsolve
echo "==> systemd unit installed and enabled"

# ── Sudoers for OTA self-restart ──────────────────────────────────────────────
echo "==> Configuring sudoers for OTA restart"
echo "stepsolve ALL=(ALL) NOPASSWD: /bin/systemctl restart stepsolve" > "$SUDOERS_FILE"
chmod 440 "$SUDOERS_FILE"

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

# ── Optional: Astrometry.net ─────────────────────────────────────────────────
if command -v solve-field &>/dev/null; then
    echo "==> astrometry.net already installed ($(command -v solve-field))"
else
    echo ""
    read -r -p "Install astrometry.net via apt? (~200 MB) [y/N]: " yn
    if [[ "${yn,,}" == "y" ]]; then
        apt-get install -y astrometry.net
        echo "==> astrometry.net installed. Download index files separately — see PI_SETUP.md."
    else
        echo "    Skipped. Install later with: sudo apt-get install -y astrometry.net"
    fi
fi

# ── Python solver venv (Tetra3) ───────────────────────────────────────────────
echo "==> Setting up Python solver venv"
bash "$INSTALL_DIR/scripts/setup_solver_venv.sh"

# ── Start service ─────────────────────────────────────────────────────────────
echo "==> Starting stepsolve service"
systemctl start stepsolve

# ── Summary ───────────────────────────────────────────────────────────────────
echo ""
echo "======================================================"
echo "  Installation complete!"
echo ""
echo "  Dashboard:  http://stepsolve.local:5001"
echo "  LX200:      stepsolve.local:5002"
echo ""
echo "  Service status:"
systemctl status stepsolve --no-pager -l || true
echo ""
echo "  Logs: journalctl -fu stepsolve"
echo ""
echo "  Next steps — update appsettings.json with:"
echo "    Solver:Tetra3:IndexPath  (see tetra3 db path printed above)"
echo "    Solver:Astrometry:IndexPath  /var/lib/stepsolve/indexes/astrometry"
echo "======================================================"
