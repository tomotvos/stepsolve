#!/usr/bin/env bash
# Deploy StepSolve to a Raspberry Pi.
# Usage:
#   bash scripts/deploy.sh               # build + rsync + restart
#   bash scripts/deploy.sh --install     # first-time: also runs install.sh on the Pi
#
# Override the Pi host: PI_HOST=pi@192.168.1.50 bash scripts/deploy.sh
set -euo pipefail

PI_HOST="${PI_HOST:-pi@rpiskysolve.local}"
PUBLISH="src/bin/Release/net10.0/linux-arm64/publish"

echo "==> Building for linux-arm64"
dotnet publish src/StepSolve.csproj -c Release -r linux-arm64 --self-contained

echo "==> Preparing install directory on Pi"
ssh "$PI_HOST" 'sudo mkdir -p /usr/local/lib/stepsolve && sudo chown -R "$(id -un)":"$(id -gn)" /usr/local/lib/stepsolve'

echo "==> Syncing to $PI_HOST"
rsync -az --progress "$PUBLISH/"  "$PI_HOST:/usr/local/lib/stepsolve/"
rsync -az scripts/                "$PI_HOST:/usr/local/lib/stepsolve/scripts/"
rsync -az deploy/                 "$PI_HOST:/usr/local/lib/stepsolve/deploy/"

echo "==> Restoring ownership to stepsolve"
ssh "$PI_HOST" 'sudo chown -R stepsolve:stepsolve /usr/local/lib/stepsolve'

if [[ "${1:-}" == "--install" ]]; then
    echo "==> Running installer on Pi"
    ssh "$PI_HOST" "sudo /usr/local/lib/stepsolve/deploy/install.sh"
else
    echo "==> Restarting service"
    ssh "$PI_HOST" "sudo systemctl restart stepsolve"
    echo "==> Done. Dashboard: http://stepsolve.local:5001"
fi
