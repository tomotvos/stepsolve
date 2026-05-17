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

echo "==> Installing Tetra3 (pinned)"
"$VENV_DIR/bin/pip" install --quiet -r "$SCRIPT_DIR/requirements-solvers.txt"

echo "==> Copying solver scripts to $SOLVERS_DIR"
cp "$SCRIPT_DIR/tetra3_solve_service.py" "$SOLVERS_DIR/"
chmod +x "$SOLVERS_DIR/tetra3_solve_service.py"

TETRA3_DB=$("$VENV_DIR/bin/python" -c \
    "import tetra3, os; print(os.path.join(os.path.dirname(tetra3.__file__), 'data', 'default_database'))" \
    2>/dev/null || echo "unknown")

echo "==> Done. Solver venv ready at $VENV_DIR"
echo ""
echo "==> Tetra3 default database path:"
echo "    $TETRA3_DB"
echo "    Set Solver:Tetra3:IndexPath to this value in appsettings.json"
