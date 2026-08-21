# StepSolve

A headless plate-solving appliance for Raspberry Pi. It continuously captures sky images, solves them to determine exact telescope pointing (RA/Dec), and publishes those coordinates over LX200 TCP for SkySafari — optionally syncing them to an OnStepX mount controller.

**The key principle:** the product is the headless service, not the UI. It starts at boot, runs unattended, and survives power cuts. The web dashboard is a phone-friendly "how's it going?" window.

## System context

```
                    ┌───────────┐
                    │ SkySafari │
                    └─────┬─────┘
                Go To     │     ▲
                (LX200)   ▼     │ Position readout
                    ┌───────────┐         ┌──────────────┐
                    │  OnStep   │◄────────│  StepSolve   │
                    │  (mount)  │  :CM#   │  (this app)  │
                    └─────┬─────┘         └──────▲───────┘
                          │ Move                 │ Capture & solve
                          ▼                      │
                    ┌───────────┐         ┌──────┴───────┐
                    │ Telescope │────────►│  Pi Camera   │
                    └───────────┘         └──────────────┘
```

SkySafari connects independently to OnStep (for Go To) and StepSolve (read-only position validation). StepSolve corrects OnStep's alignment after each solve via `:CM#`.

## Operating modes

| Mode | Capture | Solve | LX200 |
|------|---------|-------|-------|
| **Solve** | Continuous | Yes | Publishes solved RA/Dec |
| **Demo** | No | Yes (bundled images) | Publishes real solve results |
| **Idle** | No | No | Returns last known position |

Demo mode runs the real solver against bundled sky images — useful for testing the full pipeline on the Pi without a live sky.

## Development (macOS)

```bash
# Run tests (no hardware or star catalogs needed)
dotnet test tests/

# Run the app (starts idle by default in Development environment)
dotnet run --project src/

# Web dashboard
open http://localhost:5001

# Test LX200 protocol
echo -n ':GR#' | nc localhost 5002
```

The app detects macOS and skips `rpicam-still`. In Solve mode on Mac, it cycles through bundled demo images instead of capturing.

## Deployment (Raspberry Pi)

See **[`deploy/PI_SETUP.md`](deploy/PI_SETUP.md)** for the full setup guide.

**First install** — flash Pi OS, then from your Mac:

```bash
PI_HOST=pi@rpistepsolve.local bash scripts/deploy.sh --install
```

**Subsequent deploys** (build + rsync + restart in ~30 s):

```bash
bash scripts/deploy.sh
```

**Cut a release** (builds, packages, and publishes to GitHub Releases — users can then update via the dashboard):

```bash
bash scripts/release.sh v1.0.0
```

The service is a single self-contained binary managed by systemd. No runtime dependencies on the Pi beyond `rpicam-still` (bundled with Raspberry Pi OS) and optionally `solve-field` for the Astrometry backend.

## Solver backends

Three backends are supported, selected via `Solver:Backend` in configuration:

| Backend | Binary | Notes |
|---------|--------|-------|
| `astrometry` | `solve-field` | Optional; requires index files. |
| `tetra3` | Python subprocess | Default. Field-tested on Pi; fast and reliable in light-polluted conditions. |
| `cedar` | Python subprocess | Tetra3 fork with optimised database; back-end wired, not in UI yet. |

Backend switches take effect on the next solve cycle — no restart needed.

## Configuration

Settings live in `appsettings.json` (defaults), `appsettings.Development.json` (Mac overrides), and `appsettings.runtime.json` (persisted UI changes). The web dashboard's Settings panel writes to the runtime file; those values survive restarts and override the defaults.

Key settings:

| Setting | Default | Description |
|---------|---------|-------------|
| `StepSolve:Mode` | `solve` | Operating mode |
| `Solver:Backend` | `astrometry` | Active solver |
| `Solver:FovEstimateDeg` | `0` | Camera diagonal FOV — set this for your hardware |
| `Camera:ShutterUs` | `1000000` | Shutter speed in microseconds |
| `OnStep:Enabled` | `false` | Enable mount sync after each solve |

## Ports

| Port | Protocol | Purpose |
|------|----------|---------|
| 5001 | HTTP / WebSocket | Web dashboard and REST API |
| 5002 | TCP (LX200) | SkySafari connection |

Both are advertised via mDNS (`stepsolve.local`) when Avahi is configured.

## Documentation

- [`docs/PRD.md`](docs/PRD.md) — full specification: API reference, solver details, LX200 protocol, deployment, architecture
- [`docs/REWRITE_PLAN.md`](docs/REWRITE_PLAN.md) — migration rationale from SkySolve (Python predecessor)
