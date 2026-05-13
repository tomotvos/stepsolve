#!/usr/bin/env bash
# Creates an isolated Python venv and installs Cedar and Tetra3 solver dependencies.
# Safe to re-run — skips venv creation if it already exists.
#
# Usage: sudo bash scripts/setup_solver_venv.sh
# Override paths with environment variables:
#   VENV_DIR=/custom/path SOLVERS_DIR=/custom/path bash setup_solver_venv.sh

set -euo pipefail

VENV_DIR="${VENV_DIR:-/var/lib/stepsolve/solvers/.venv}"
SOLVERS_DIR="${SOLVERS_DIR:-/var/lib/stepsolve/solvers}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "==> Solver venv setup"
echo "    VENV_DIR:    $VENV_DIR"
echo "    SOLVERS_DIR: $SOLVERS_DIR"

mkdir -p "$SOLVERS_DIR"

if [ ! -d "$VENV_DIR" ]; then
    echo "==> Creating Python venv at $VENV_DIR"
    python3 -m venv "$VENV_DIR"
else
    echo "==> Venv already exists at $VENV_DIR, skipping creation"
fi

echo "==> Upgrading pip"
"$VENV_DIR/bin/pip" install --quiet --upgrade pip

echo "==> Installing Cedar and Tetra3 dependencies"
"$VENV_DIR/bin/pip" install --quiet \
    cedar-detect \
    "git+https://github.com/esa/tetra3.git"

echo "==> Copying solver scripts to $SOLVERS_DIR"
cp "$SCRIPT_DIR/cedar_solve_service.py"  "$SOLVERS_DIR/"
cp "$SCRIPT_DIR/tetra3_solve_service.py" "$SOLVERS_DIR/"
chmod +x "$SOLVERS_DIR/cedar_solve_service.py"
chmod +x "$SOLVERS_DIR/tetra3_solve_service.py"

echo "==> Done. Solver venv ready at $VENV_DIR"
