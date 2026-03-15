# StepSolve — Product Requirements Document

> Version 1.0 — March 15, 2026
> Successor to SkySolve Next (Python). See [REWRITE_PLAN.md](REWRITE_PLAN.md) for migration rationale.

---

## 1. Product Summary

StepSolve is a **headless plate-solving appliance** for Raspberry Pi. It continuously captures sky images, solves them to determine telescope pointing (RA/Dec), and publishes those coordinates over LX200 TCP for SkySafari and optionally syncs them to an OnStepX mount controller. A lightweight web dashboard provides diagnostics and configuration.

**Key principle:** The product is the headless service, not the UI. It starts at boot, runs unattended for months, and survives power cuts. The web dashboard is a "how's it going?" window on your phone.

### System context

The end goal is **plate-solving based telescope tracking control** — a closed loop where solving continuously corrects the mount's understanding of where it's pointing:

```
                        ┌───────────┐
                        │ SkySafari │
                        └─────┬─────┘
                    Go To     │     ▲
                    (LX200)   ▼     │ Validation Path
                        ┌───────────┐         ┌──────────────┐
                        │  OnStep   │◄────────│ Plate Solver │
                        │ (mount    │ Calibrate│ (StepSolve)  │
                        │ controller│  :CM#   │              │
                        └─────┬─────┘         └──────▲───────┘
                              │ Move                 │ Capture
                              ▼                      │ & Solve
                        ┌───────────┐         ┌──────┴───────┐
                        │ Telescope │────────►│Image Capture │
                        └───────────┘         │ (rpicam-still│)
                         Continuous Solve     └──────────────┘
```

**The loop:** The telescope points at the sky → the camera captures an image → the plate solver determines the exact RA/Dec → OnStep's alignment is corrected (`:CM#` sync) → the telescope's tracking improves → next capture is more accurate. SkySafari connects independently to both OnStep (for Go To commands) and StepSolve (read-only validation of where the telescope is actually pointing).

### What it replaces

StepSolve replaces SkySolve Next, a Python/FastAPI application with these issues:
- Broken abstractions (solver interface violated by its own implementation)
- Fragile file-based IPC between web and worker processes
- XSS vulnerability via `innerHTML` in the frontend
- Environment version mismatches generating thousands of deprecation warnings
- Over-engineered logging infrastructure
- ~1,670 lines of code not worth refactoring

### Technology choice

- **.NET 9** with Native AOT compilation → single ~15 MB self-contained `linux-arm64` binary
- No Python runtime required for core functionality
- Python only used for Cedar/Tetra3 solver wrappers (long-running pipe processes in an isolated venv)
- No frontend build toolchain — static HTML/JS/CSS served by Kestrel

---

## 2. Goals & Non-Goals

### Goals

1. **Feature parity with SkySolve** — solve images and publish RA/Dec to SkySafari via LX200/TCP
2. **Appliance reliability** — start at boot, survive power cuts, run for months unattended
3. **Single-process architecture** — no file-based IPC, no multi-process coordination
4. **Multiple solver backends** — Astrometry.net (default), Cedar, Tetra3, with configurable index paths
5. **OnStep sync** — optional mount alignment after each solve
6. **Diagnostic web dashboard** — phone-friendly, day/night theme, real-time log streaming
7. **Simple deployment** — `scp` one binary + restart service
8. **Mac development** — build, test, and run the full application on macOS without Pi hardware

### Non-Goals

- The LX200 server does **not** slew the mount — it only reports position
- No multi-user authentication (local network appliance)
- No USB camera support (rpicam-still only for capture; mock on Mac)
- No image processing pipeline (debayer, star detection, quality filters)
- No Prometheus metrics endpoint
- No Blazor, Razor, or frontend framework — vanilla JS only
- No npm, webpack, or frontend build step

---

## 3. Operating Modes

| Mode | Capture | Solve | LX200 | OnStep | Description |
|------|---------|-------|-------|--------|-------------|
| **Solve** | Continuous | Yes | Publishes solved RA/Dec | Optional sync | Primary field mode |
| **Demo** | No | No | Publishes simulated sweep | No | Test SkySafari/UI wiring without hardware |
| **Idle** | No | No | Returns last known position | No | Service running but not capturing |

Mode is set via configuration or the web dashboard.

---

## 4. System Architecture

### Single-process model

```
┌──────────────────────────────────────────┐
│              StepSolve Process            │
│                                          │
│  ┌─────────────────────────────────────┐ │
│  │ StepSolveService (BackgroundService)│ │
│  │                                     │ │
│  │  loop:                              │ │
│  │    capture (rpicam-still)           │ │
│  │    solve (solve-field / cedar pipe) │ │
│  │    if solved:                       │ │
│  │      update SolveState (in-memory)  │ │
│  │      notify WebSocket clients       │ │
│  │      sync to OnStep if enabled      │ │
│  │    sleep until next cycle           │ │
│  └─────────────────────────────────────┘ │
│                                          │
│  ┌────────────────┐ ┌────────────────┐   │
│  │ LX200 Server   │ │ HTTP/WS Server │   │
│  │ TCP :5002      │ │ HTTP :5001     │   │
│  │ reads SolveState│ │ reads SolveState│  │
│  └────────────────┘ └────────────────┘   │
│                                          │
│  ┌────────────────┐                      │
│  │ OnStep Client  │                      │
│  │ TCP → :9998    │                      │
│  │ writes on solve│                      │
│  └────────────────┘                      │
└──────────────────────────────────────────┘
```

One process. One binary. One systemd unit. All components share in-memory state via `SolveState` protected by a `lock` or `Channel<T>`.

### Component responsibilities

| Component | Responsibility |
|-----------|---------------|
| `Program.cs` | Host setup, minimal API route registration, static file serving (~60 lines) |
| `StepSolveService.cs` | `BackgroundService`: capture → solve → publish loop |
| `Lx200Server.cs` | TCP listener for SkySafari on port 5002. Thread-per-client. |
| `OnStepClient.cs` | TCP client to OnStepX on port 9998. Sync RA/Dec after solve. |
| `ISolver.cs` | Interface: `Task<SolveResult> SolveAsync(string imagePath, SolveHints? hints)` |
| `AstrometrySolver.cs` | Shells out to `solve-field`, parses stdout |
| `CedarSolver.cs` | Manages long-running `cedar_solve_service.py` via stdin/stdout pipe |
| `Tetra3Solver.cs` | Same pipe pattern for `tetra3_solve_service.py` |
| `CameraCapture.cs` | Shells out to `rpicam-still` (mock on macOS) |
| `StepSolveOptions.cs` | Strongly-typed config bound from `appsettings.json` |
| `SolveResult.cs` | Record struct for solve output |
| `SolveState.cs` | Thread-safe shared state: latest RA/Dec, confidence, timestamp |

---

## 5. Interfaces

### 5.1 LX200 TCP Server (Port 5002)

Read-only Meade LX200 protocol server for SkySafari.

**Design rule:** The LX200 server must **never slew** the mount. It only reports the latest solved position.

#### Supported commands

**Read queries:**

| Command | Response Format | Description |
|---------|----------------|-------------|
| `:GR#` | `HH:MM:SS#` | Right Ascension |
| `:RS#` | `HH:MM:SS#` | RA (SkySafari alternate query) |
| `:GD#` | `±DD*MM:SS#` | Declination (asterisk after degrees) |
| `:GVP#` | `StepSolve#` | Product name |
| `:GVN#` | `1.0#` | Version |
| `:GVD#` | `MM/DD/YY#` | Date |
| `:GVT#` | `HH:MM:SS#` | Time |
| `:GC#` | `MM/DD/YY#` | Calendar date |
| `:GL#` | `HH:MM:SS#` | Local time |
| `:U#` | `1` | Precision toggle (note: returns `1`, not `1#`) |

**Write commands (accepted, ACK only):**

| Command | Response | Notes |
|---------|----------|-------|
| `:SC...#` | `1` | Set calendar — accepted but ignored |
| `:SL...#` | `1` | Set local time — accepted but ignored |
| `:St...#` | `1` | Set latitude — accepted but ignored |
| `:Sg...#` | `1` | Set longitude — accepted but ignored |

**Motion commands (ignored, no slew):**

| Command | Response |
|---------|----------|
| `:MS#` | `0` |
| `:Mn#`, `:Me#`, `:Ms#`, `:Mw#` | `0` |

**Unknown commands:** Return `#`

#### Coordinate formatting

```
RA (degrees → HH:MM:SS):
  hours = ra_deg / 15.0
  Format: {HH:02d}:{MM:02d}:{SS:02d}

Dec (degrees → ±DD*MM:SS):
  sign = "+" if dec_deg >= 0 else "-"
  Format: {sign}{DD:02d}*{MM:02d}:{SS:02d}
```

#### Protocol handling

- **Delimiter:** Commands terminate with `#`
- **Batched commands:** Buffer incoming data, split on `#`, process each command independently
- **Partial reads:** Accumulate in buffer until `#` received
- **Case handling:** Normalize to uppercase for matching
- **Prefix normalization:** Prepend `:` if missing
- **Socket timeout:** 30 seconds (keeps connections alive during SkySafari polling)
- **No-solve state:** Return `00:00:00#` for RA and `+00*00:00#` for Dec when no solve available
- **Thread model:** One `Task` per connected client

#### SkySafari-specific notes

- SkySafari connects as "Meade LX200 Classic"
- It polls `:GR#:GD#` continuously (often batched in a single TCP read)
- It sends `:U#` on connect for precision toggle
- Some versions send `:RS#` instead of `:GR#` for RA
- Connection is long-lived; expect periodic polls with no close/reconnect

### 5.2 HTTP API (Port 5001)

Minimal API endpoints served by Kestrel.

#### Status & Control

| Method | Path | Request | Response | Description |
|--------|------|---------|----------|-------------|
| `GET` | `/` | — | HTML | Diagnostic dashboard |
| `GET` | `/status` | — | JSON | Current state (see below) |
| `POST` | `/mode` | `{ "mode": "solve" }` | JSON | Set operating mode |
| `GET` | `/settings` | — | JSON | Current configuration |
| `POST` | `/settings` | JSON (partial) | JSON | Update configuration (merges) |

#### Solve

| Method | Path | Request | Response | Description |
|--------|------|---------|----------|-------------|
| `POST` | `/solve` | multipart image OR `?demo=1` | JSON | Solve an uploaded image on demand |
| `GET` | `/solve/image` | — | JPEG | Last solved image |

#### System

| Method | Path | Request | Response | Description |
|--------|------|---------|----------|-------------|
| `POST` | `/system/shutdown` | — | JSON | Graceful shutdown (Linux only) |
| `POST` | `/system/restart` | — | JSON | Graceful reboot (Linux only) |

#### WebSocket

| Path | Direction | Description |
|------|-----------|-------------|
| `/ws` | Server → Client | Multiplexed real-time stream |

WebSocket messages are JSON with a `type` discriminator:

```json
{ "type": "solve",  "ra": 296.94, "dec": 42.69, "confidence": 0.95, "solver": "astrometry", "solveTimeMs": 2340, "timestamp": "..." }
{ "type": "status", "mode": "solve", "state": "solving", "uptime": 3600 }
{ "type": "log",    "level": "INFO", "message": "Solve completed", "timestamp": "..." }
```

#### GET /status response

```json
{
  "mode": "solve",
  "state": "idle|capturing|solving|solved|error",
  "ra": 296.944646,
  "dec": 42.688983,
  "confidence": 0.95,
  "solver": "astrometry",
  "solveTimeMs": 2340,
  "lastSolveTimestamp": "2026-03-15T10:42:02Z",
  "uptime": 3600,
  "onstep": {
    "enabled": true,
    "connected": false,
    "lastSyncTimestamp": null,
    "lastSyncResult": null
  }
}
```

### 5.3 OnStep Client (Port 9998)

Optional TCP client that syncs solved coordinates to an OnStepX mount controller.

**Default:** Disabled. Enabled via configuration.

#### Sync protocol

After each successful solve:

```
1. :SrHH:MM:SS#       → Set target RA
2. :Sd±DD*MM:SS#      → Set target Dec
3. :CM#                → Synchronize (align mount to these coordinates)
```

Coordinate format is identical to LX200 server output.

#### Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| `OnStep:Enabled` | `false` | Enable/disable sync |
| `OnStep:Host` | `localhost` | OnStepX hostname or IP |
| `OnStep:Port` | `9998` | OnStepX TCP port (leaves 9999 free for SkySafari → mount) |
| `OnStep:SyncMode` | `sync` | `sync` (immediate) or `slew_then_sync` (slew first, then align) |
| `OnStep:MaxSyncDeltaDeg` | `5.0` | Safety: reject sync if angular distance > threshold |

#### Error handling

- 3-second socket timeout
- Retry with exponential backoff on connection failure (1s, 2s, 4s, max 30s)
- Log all sync attempts and outcomes
- Do not block the solve loop on OnStep failures — fire and continue

#### Safety

If the angular distance between the current mount position and the solved position exceeds `MaxSyncDeltaDeg`:
- Log a warning with both positions
- Skip the sync
- Report the skip in the web dashboard status

This prevents wild jumps from a faulty solve corrupting the mount's alignment.

---

## 6. Solver Backends

### 6.1 Interface

```csharp
public interface ISolver
{
    Task<SolveResult> SolveAsync(string imagePath, SolveHints? hints, CancellationToken ct);
}

public record SolveResult(
    double RaDeg,
    double DecDeg,
    double? RollDeg,
    double? PlateScaleArcsecPerPx,
    double Confidence,
    TimeSpan SolveTime,
    string SolverName
);

public record SolveHints(double RaDeg, double DecDeg, double RadiusDeg);
```

### 6.2 Astrometry.net (`AstrometrySolver`)

Shells out to `solve-field` (C binary, installed separately on Pi).

#### 2-phase solve strategy

**Phase 1 — Hinted solve:**
- If a previous solve exists and is within `HintTimeout` (default 10s), use it as a positional hint
- Flags: `--ra {hint_ra} --dec {hint_dec} --radius {radius}`
- Always generates an XY list (`--keep-xylist`) for Phase 2 fallback
- Success: `.solved` file exists AND RA/Dec are non-zero

**Phase 2 — Unhinted fallback:**
- If Phase 1 fails and the XY file exists, re-run solve-field on the XY data without positional constraints
- Only runs if `EnableFallback` is true (default: true)

#### solve-field command

```
solve-field <image_path>
    --overwrite
    --no-plots
    --new-fits none
    --sigma 5              # star detection threshold
    --depth 20             # index search depth
    --uniformize 0         # disable star clustering
    --no-remove-lines
    --match none
    --corr none
    --rdls none
    --ra <hint_ra>         # Phase 1 only
    --dec <hint_dec>       # Phase 1 only
    --radius <radius>      # Phase 1 only (default 20.0 deg)
    --keep-xylist <base>.xy
```

#### Output parsing (regex)

```
RA,Dec extraction:
  Primary:  RA,Dec\s*=\s*\(([-\d.]+),\s*([-\d.]+)\)
  Fallback: Field center: \(RA,Dec\) = \(([-\d.]+),\s*([-\d.]+)\)

Confidence:
  Line containing "Confidence:" → split and take second token

Timestamp removal:
  ^\[\d{2}:\d{2}:\d{2}\]\s*
```

#### Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| `SolveFieldPath` | `solve-field` | Path to the solve-field binary |
| `IndexPath` | `/usr/share/astrometry` | Astrometry.net index directory |
| `Timeout` | `60s` | Per-solve timeout |
| `Sigma` | `5` | Star detection threshold |
| `Depth` | `20` | Index search depth |
| `SolveRadius` | `20.0` | Hint search radius (degrees) |
| `HintTimeout` | `10s` | Max age of previous solve to use as hint |
| `EnableFallback` | `true` | Allow Phase 2 unhinted solve |

### 6.3 Cedar (`CedarSolver`)

Cedar-solve is a Python package with no CLI. StepSolve runs it as a **long-running subprocess** connected via stdin/stdout pipes.

#### Architecture

1. At service startup, StepSolve spawns `python cedar_solve_service.py` once
2. Python process loads cedar-solve and its star database into memory (one-time cost, ~2s)
3. Per solve: StepSolve writes a JSON request to stdin, reads a JSON response from stdout
4. Per-solve overhead: ~1-2ms (vs ~210ms for spawning a new process each time)
5. Cedar's own solve time: ~10ms → total ~12ms per solve

#### Pipe protocol

Request (StepSolve → Python, one JSON object per line):
```json
{"image_path": "/var/lib/stepsolve/images/capture.jpg", "index_path": "/var/lib/stepsolve/indexes/cedar-default"}
```

Response (Python → StepSolve, one JSON object per line):
```json
{"ra_deg": 296.94, "dec_deg": 42.69, "roll_deg": 12.3, "confidence": 0.98, "solve_time_ms": 10}
```

Error response:
```json
{"error": "Failed to solve: no matching stars"}
```

#### Wrapper script (`cedar_solve_service.py`, ~50 lines)

```python
# Reads JSON from stdin line-by-line
# Loads cedar-solve database once on startup
# For each request: solve image, write JSON result to stdout
# Never exits unless stdin closes or fatal error
```

### 6.4 Tetra3 (`Tetra3Solver`)

Same long-running pipe pattern as Cedar. Wrapper script `tetra3_solve_service.py` loads tetra3 database once, reads JSON requests, writes JSON responses.

### 6.5 Solver selection & fallback

Configured in `appsettings.json`:

```json
{
  "Solver": {
    "Backend": "astrometry",
    "Astrometry": { ... },
    "Cedar": { ... },
    "Tetra3": { ... }
  }
}
```

- The configured `Backend` is the primary solver
- If the primary fails, no automatic fallback to a different backend (fallback is Phase 1 → Phase 2 within Astrometry only)
- Backend can be changed at runtime via settings API or config hot-reload

### 6.6 Index management

Each backend has a configurable index/database path:

```json
{
  "Solver": {
    "Astrometry": { "IndexPath": "/usr/share/astrometry" },
    "Cedar": { "IndexPath": "/var/lib/stepsolve/indexes/cedar-default" },
    "Tetra3": { "IndexPath": "/var/lib/stepsolve/indexes/tetra3-default" }
  }
}
```

- Index path is passed to solver on each solve (astrometry via `--config`, Cedar/Tetra3 via JSON request)
- Hot-swappable: change path in config → takes effect on next solve cycle via `IOptionsMonitor<T>`
- Multiple index sets can coexist in separate directories (e.g., `cedar-wide-field`, `cedar-narrow`)
- Index files are **not** shipped with the application — downloaded separately during install

#### Pi directory layout

```
/var/lib/stepsolve/
├── indexes/
│   ├── astrometry/        # 4100/4200 series index files
│   ├── cedar-default/     # Cedar star database
│   └── tetra3-default/    # Tetra3 star database
├── images/                # Captured/solved images (runtime)
└── logs/                  # Log files (if file logging enabled)
```

---

## 7. Camera Capture

### 7.1 Raspberry Pi (field)

Shell out to `rpicam-still`:

```bash
rpicam-still -o <output_path> --shutter <us> --gain <value> --width <w> --height <h> --immediate --nopreview
```

No Picamera2 Python dependency. Same subprocess pattern used for `solve-field`.

#### Parameters

| Setting | Default | Description |
|---------|---------|-------------|
| `Camera:ShutterUs` | `1000000` (1s) | Shutter speed in microseconds |
| `Camera:Gain` | `8.0` | Analog gain (replaces ISO) |
| `Camera:Width` | `1280` | Image width |
| `Camera:Height` | `960` | Image height |
| `Camera:OutputFormat` | `jpg` | Output format |

#### Capture cadence

- In Solve mode, capture runs continuously
- Next capture starts as soon as the previous image is handed to the solver
- Only the most recent image is solved — if the solver is still busy, the older image is discarded
- This means capture cadence = max(capture_time, solve_time)

### 7.2 macOS (development)

No camera hardware available. Options:
- **Demo mode:** Generates simulated RA/Dec sweep (no image capture needed)
- **Mock capture:** Returns a bundled test image for solver testing
- **Upload:** Web dashboard accepts image upload for on-demand solving

The `CameraCapture` class detects the platform and uses the appropriate strategy.

### 7.3 Preview image

After each capture, the image (or a downscaled version) is available at `GET /solve/image` for the web dashboard to display.

---

## 8. Configuration

### 8.1 Config file: `appsettings.json`

```json
{
  "StepSolve": {
    "Mode": "solve",
    "WebPort": 5001,
    "Lx200Port": 5002
  },
  "Solver": {
    "Backend": "astrometry",
    "HintTimeout": 10,
    "SolveRadius": 20.0,
    "EnableFallback": true,
    "Astrometry": {
      "SolveFieldPath": "solve-field",
      "IndexPath": "/usr/share/astrometry",
      "Timeout": 60,
      "Sigma": 5,
      "Depth": 20
    },
    "Cedar": {
      "PythonPath": "/var/lib/stepsolve/solvers/.venv/bin/python",
      "ScriptPath": "/var/lib/stepsolve/solvers/cedar_solve_service.py",
      "IndexPath": "/var/lib/stepsolve/indexes/cedar-default"
    },
    "Tetra3": {
      "PythonPath": "/var/lib/stepsolve/solvers/.venv/bin/python",
      "ScriptPath": "/var/lib/stepsolve/solvers/tetra3_solve_service.py",
      "IndexPath": "/var/lib/stepsolve/indexes/tetra3-default"
    }
  },
  "Camera": {
    "ShutterUs": 1000000,
    "Gain": 8.0,
    "Width": 1280,
    "Height": 960,
    "OutputFormat": "jpg"
  },
  "OnStep": {
    "Enabled": false,
    "Host": "localhost",
    "Port": 9998,
    "SyncMode": "sync",
    "MaxSyncDeltaDeg": 5.0
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information"
    }
  }
}
```

### 8.2 Config binding

Strongly typed via `IOptions<T>` / `IOptionsMonitor<T>`:

```csharp
public class StepSolveOptions { ... }
public class SolverOptions { ... }
public class CameraOptions { ... }
public class OnStepOptions { ... }
```

Hot-reload: `IOptionsMonitor<T>` detects file changes and reloads without restart. Settings changes via the web API write to `appsettings.json` and are picked up on the next cycle.

### 8.3 Environment overrides

.NET configuration supports environment variable overrides with `__` as separator:

```bash
StepSolve__Mode=demo
Solver__Backend=cedar
OnStep__Enabled=true
OnStep__Host=192.168.0.1
```

Useful for systemd `Environment=` directives or `.env` files.

---

## 9. Web Dashboard (UX)

### 9.1 Design philosophy

- **Mobile-first** — primary use is checking status on a phone in the field
- **Minimal** — 5 interactions: view status, trigger manual solve, change mode, edit settings, view logs
- **No framework** — vanilla HTML/JS/CSS, no build step
- **Night mode** — muted red palette for astronomy use, auto-toggleable based on local time
- **Accessible** — keyboard navigable, ARIA live announcements, visible focus outlines
- **Lightweight** — total payload under 200 KB for Pi-hosted pages

### 9.2 Layout

**Mobile (single column):**

```
┌──────────────────────────┐
│ StepSolve    [Mode ▼] 🌙 │  ← Header: mode selector, night toggle
├──────────────────────────┤
│                          │
│    RA: 19h 47m 51s       │  ← Last solve result
│   Dec: +42° 41' 20"      │
│  Conf: 0.95              │
│ Solve: 2.3s (astrometry) │
│  Time: 10:42:02          │
│                          │
├──────────────────────────┤
│  [  Solve Now  ]         │  ← Manual solve button (large touch target)
├──────────────────────────┤
│ OnStep: ● Connected      │  ← OnStep status (if enabled)
│ Last sync: 10:42:02      │
├──────────────────────────┤
│ ▸ Settings               │  ← Expandable sections
│ ▸ Logs                   │
└──────────────────────────┘
```

**Desktop (split view):**

```
┌────────────────────┬─────────────────────┐
│ StepSolve  [Mode]  │                     │
├────────────────────┤  Last Solve          │
│                    │  RA: 19h 47m 51s    │
│   Image Preview    │  Dec: +42° 41' 20"  │
│                    │  Conf: 0.95         │
│                    │  Time: 2.3s         │
│                    │                     │
│  [  Solve Now  ]   │  OnStep Status      │
├────────────────────┤                     │
│ Settings           │  Logs               │
│  Solver: [▼]       │  10:42:02 INFO ...  │
│  Camera: ...       │  10:42:01 INFO ...  │
│  OnStep: ...       │  10:42:00 DEBUG ... │
└────────────────────┴─────────────────────┘
```

### 9.3 Status display

| Field | Source | Update |
|-------|--------|--------|
| RA | Last solve | WebSocket `solve` message |
| Dec | Last solve | WebSocket `solve` message |
| Confidence | Last solve | WebSocket `solve` message |
| Solve time | Last solve | WebSocket `solve` message |
| Solver | Last solve | WebSocket `solve` message |
| Timestamp | Last solve | WebSocket `solve` message |
| Mode | Current config | WebSocket `status` message |
| State | Worker state | WebSocket `status` message (idle/capturing/solving) |
| OnStep status | Connection state | WebSocket `status` message |
| Stale indicator | Computed client-side | If last solve > 5s ago, show yellow indicator |

### 9.4 Interactions

1. **Mode selector** — dropdown or radio: Solve / Demo / Idle
2. **Solve Now** — button triggers `POST /solve?demo=1` (demo) or upload dialog (file) — large touch target (min 44×44 px)
3. **Night mode toggle** — switches CSS custom properties to muted red palette; optionally auto-toggles by local time
4. **Settings panel** — expandable sections for solver, camera, OnStep config. Saves via `POST /settings`.
5. **Log stream** — scrolling log viewer powered by WebSocket. Pause/resume/clear controls. Color-coded by level (DEBUG grey, INFO white, WARNING yellow, ERROR red).

### 9.5 Night mode

- Uses CSS custom properties for all colors
- Night palette: dark red/maroon background, dim red text, no blue light
- Toggle via button in header
- Optional auto-enable based on configurable local time range
- Must maintain sufficient contrast (WCAG AA for large text at minimum)

### 9.6 Real-time updates

Single WebSocket connection at `/ws` replaces polling:

```javascript
const ws = new WebSocket(`ws://${location.host}/ws`);
ws.onmessage = (e) => {
    const msg = JSON.parse(e.data);
    switch (msg.type) {
        case 'solve':  updateSolveDisplay(msg); break;
        case 'status': updateStatusBadge(msg); break;
        case 'log':    appendLogEntry(msg); break;
    }
};
```

Reconnect with exponential backoff on disconnect. Fallback to `GET /status` polling (5s interval) if WebSocket is unavailable.

### 9.7 Security

- All output rendered via `textContent`, never `innerHTML` (prevents XSS)
- No external CDN dependencies — all assets served from the binary
- System control endpoints (`/system/shutdown`, `/system/restart`) only available on Linux

### 9.8 File structure

```
wwwroot/
├── index.html          # Single-page dashboard
├── css/
│   └── app.css         # Day/night themes, responsive layout
└── js/
    ├── app.js          # Init, status display, mode control
    ├── api.js          # Fetch wrapper for REST endpoints
    └── logs.js         # WebSocket log stream viewer
```

---

## 10. Deployment

### 10.1 Build (on Mac)

```bash
dotnet publish -c Release -r linux-arm64 --self-contained -p:PublishAot=true
```

Produces a single ~15 MB binary at `bin/Release/net9.0/linux-arm64/publish/stepsolve`.

### 10.2 Deploy (to Pi)

```bash
scp bin/Release/net9.0/linux-arm64/publish/stepsolve pi@stepsolve.local:/usr/local/bin/
ssh pi@stepsolve.local "sudo systemctl restart stepsolve"
```

### 10.3 Systemd unit

```ini
[Unit]
Description=StepSolve plate solver
After=network.target

[Service]
ExecStart=/usr/local/bin/stepsolve
WorkingDirectory=/var/lib/stepsolve
Restart=always
RestartSec=5
User=stepsolve
Environment=DOTNET_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
```

### 10.4 mDNS / Avahi

```xml
<!-- /etc/avahi/services/stepsolve-http.service -->
<service-group>
  <name>StepSolve Web</name>
  <service>
    <type>_http._tcp</type>
    <port>5001</port>
  </service>
</service-group>
```

```xml
<!-- /etc/avahi/services/stepsolve-lx200.service -->
<service-group>
  <name>StepSolve LX200</name>
  <service>
    <type>_lx200._tcp</type>
    <port>5002</port>
  </service>
</service-group>
```

### 10.5 Install script

`deploy/install.sh` automates Pi setup:

1. Create `stepsolve` system user
2. Create `/var/lib/stepsolve/` directory structure
3. Copy binary to `/usr/local/bin/stepsolve`
4. Install systemd unit and enable it
5. Install Avahi service files
6. Create Python venv for solver wrappers (`pip install cedar-solve tetra3`)
7. Download index files (prompted, not automatic — different optics need different indexes)
8. Start the service

### 10.6 Appliance behavior

- `systemctl enable stepsolve` → starts on boot
- LX200 server listens → SkySafari finds it via mDNS
- Solve loop runs continuously → coordinates stay current
- Phone check: `http://stepsolve.local:5001` → see RA/Dec, confidence, last solve time
- Power cut → Pi reboots → service starts → solving resumes
- No user interaction needed for normal operation

### 10.7 Networking & Hotspot

- Autohotspot scripts carried forward from SkySolve legacy
- Pi acts as access point in the field (no WiFi available)
- Pi joins existing WiFi at home (for SSH, updates)
- NetworkManager + GPIO or timer-based switching
- StepSolve binds `0.0.0.0` — works in both modes without configuration changes
- mDNS works in both modes (Avahi advertises on all interfaces)

---

## 11. Development on macOS

### 11.1 Prerequisites

- .NET 9 SDK (installed: 9.0.305)
- No Pi hardware, camera, or solve-field needed for basic development

### 11.2 Build & Run

```bash
# Build
dotnet build

# Run (development mode)
dotnet run

# Run with specific settings
dotnet run -- --urls "http://localhost:5001"
```

The application detects macOS and:
- Skips `rpicam-still` (uses mock capture or demo mode)
- Skips `solve-field` if not installed (demo mode works without it)
- Astrometry.net can be installed on Mac for integration testing: `brew install astrometry-net`
- LX200 server starts and can be tested with any TCP client (`nc localhost 5002`)

### 11.3 Testing

```bash
# Run all tests
dotnet test

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"
```

#### Test strategy

| Layer | What to test | How |
|-------|-------------|-----|
| Solvers | Command construction, output parsing, hint logic | Unit tests with captured solve-field output |
| LX200 | Command parsing, response formatting, batched input | Unit tests with simulated TCP streams |
| OnStep | Command construction, safety threshold, retry logic | Unit tests with mock TCP |
| API | Endpoint responses, settings persistence | Integration tests with `WebApplicationFactory<T>` |
| WebSocket | Message format, client connection lifecycle | Integration tests |
| Config | Option binding, hot-reload, environment overrides | Unit tests |
| End-to-end | Demo mode full cycle | Integration test: start host → verify LX200 responses |

#### Testing LX200 with SkySafari

On Mac, point SkySafari (macOS or iOS on same network) at `localhost:5002` as "Meade LX200 Classic". In demo mode, SkySafari should show a moving reticle.

### 11.4 Cross-compilation

Build the Pi binary from Mac:

```bash
dotnet publish -c Release -r linux-arm64 --self-contained -p:PublishAot=true
```

This uses .NET's cross-compilation support — no Docker, no QEMU, no remote build needed.

### 11.5 Project structure

```
stepsolve/
├── src/
│   ├── Program.cs
│   ├── StepSolveService.cs
│   ├── Lx200Server.cs
│   ├── OnStepClient.cs
│   ├── Solvers/
│   │   ├── ISolver.cs
│   │   ├── AstrometrySolver.cs
│   │   ├── CedarSolver.cs
│   │   └── Tetra3Solver.cs
│   ├── CameraCapture.cs
│   ├── StepSolveOptions.cs
│   ├── SolveResult.cs
│   ├── SolveState.cs
│   └── wwwroot/
│       ├── index.html
│       ├── css/
│       │   └── app.css
│       └── js/
│           ├── app.js
│           ├── api.js
│           └── logs.js
├── solvers/
│   ├── cedar_solve_service.py
│   ├── tetra3_solve_service.py
│   └── requirements.txt
├── tests/
│   └── StepSolve.Tests/
│       ├── Solvers/
│       ├── Lx200ServerTests.cs
│       ├── OnStepClientTests.cs
│       └── ...
├── deploy/
│   ├── stepsolve.service
│   ├── install.sh
│   └── avahi/
├── docs/
│   ├── PRD.md
│   └── REWRITE_PLAN.md
├── appsettings.json
├── appsettings.Development.json
├── stepsolve.csproj
├── .gitignore
└── README.md
```

---

## 12. Logging

### Structured logging

Use .NET's built-in `ILogger<T>` with structured messages:

```csharp
_logger.LogInformation("Solve completed: RA={Ra}, Dec={Dec}, Confidence={Confidence}, Time={SolveTimeMs}ms",
    result.RaDeg, result.DecDeg, result.Confidence, result.SolveTime.TotalMilliseconds);
```

### Log levels

| Level | Usage |
|-------|-------|
| Debug | LX200 command traffic, config reload, solver details |
| Information | Solve results, mode changes, OnStep sync outcomes |
| Warning | Stale hints, OnStep sync delta exceeded, retry attempts |
| Error | Solver failures, camera errors, OnStep connection failures |
| Critical | Service startup/shutdown failures |

### Log output

- **Console** (stdout): always enabled, captured by systemd journal
- **WebSocket**: streamed to web dashboard in real-time
- **File** (optional): configurable via `Logging` section, with rotation

No custom logging infrastructure. Use .NET's `ILoggerFactory` with standard providers.

---

## 13. Acceptance Criteria

### Must pass before v1.0

1. **SkySafari integration**
   - SkySafari connects to port 5002 as "Meade LX200 Classic"
   - Continuously reads RA/Dec via `:GR#:GD#`
   - Handles batched commands, precision toggle (`:U#`), identity queries
   - Never attempts to slew

2. **Demo mode**
   - With mode set to `demo`, LX200 server returns a smooth RA/Dec sweep
   - SkySafari shows a moving reticle
   - Web dashboard shows updating coordinates

3. **Solve mode (Astrometry)**
   - Captures image via `rpicam-still`
   - Solves via `solve-field` with 2-phase strategy
   - Publishes result to LX200 and web dashboard
   - Continuous loop with configurable cadence

4. **OnStep sync (when enabled)**
   - After each solve, sends `:Sr`, `:Sd`, `:CM#` to configured host:port
   - Respects `MaxSyncDeltaDeg` safety threshold
   - Does not block solve loop on failure

5. **Web dashboard**
   - Shows last solve RA/Dec, confidence, solver, time
   - Mode selector works
   - Night mode toggle works
   - Log stream via WebSocket works
   - Settings panel reads and writes configuration
   - Works on iPhone Safari and desktop Chrome/Firefox

6. **Appliance reliability**
   - Service starts at boot via systemd
   - Survives power cuts (restart → resume)
   - Runs for 24+ hours without memory leaks or crashes

7. **Mac development**
   - `dotnet build` succeeds on macOS
   - `dotnet run` starts the service in demo mode
   - `dotnet test` passes all unit and integration tests
   - Cross-compile to `linux-arm64` succeeds

8. **Deployment**
   - Single binary `scp` + service restart deploys to Pi
   - mDNS advertises both HTTP and LX200 services

---

## 14. Execution Phases

See [REWRITE_PLAN.md](REWRITE_PLAN.md) §8 for the phased implementation plan.

Summary:
1. **Foundation** — .NET project, config, AOT publish verification
2. **Core Solve Loop** — camera capture, solver backends, BackgroundService
3. **LX200 Server** — TCP protocol for SkySafari
4. **OnStep Client** — TCP sync to mount controller
5. **Diagnostic Dashboard** — HTML/JS/CSS web UI
6. **Networking & Hotspot** — autohotspot scripts, mDNS
7. **Deployment & Polish** — install script, systemd, hardware integration test

---

## 15. Open Questions

| Question | Status | Notes |
|----------|--------|-------|
| Default equinox (JNow vs J2000) | Open | solve-field outputs J2000. SkySafari expects JNow. May need precession. Confirm in testing. |
| Default `MaxSyncDeltaDeg` | Resolved | 5.0 degrees — conservative enough to prevent wild jumps, permissive enough for normal use |
| Minimum SkySafari command set | Resolved | `:GR#`, `:GD#`, `:U#`, identity queries, set ACKs. Confirmed by existing implementation. |

---

## 16. References

- [StepSolve Rewrite Plan](REWRITE_PLAN.md) — architecture decisions and migration rationale
- [SkySolve Legacy](https://github.com/githubdoe/skysolve) — original implementation
- [Meade LX200 Command Set](https://www.meade.com/support/LX200CommandSet.pdf)
- [OnStep Wiki: Connections & Ports](https://onstep.groups.io/g/main/wiki/3863)
- [Astrometry.net Documentation](http://astrometry.net/doc/)
- [.NET Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- [ASP.NET Minimal APIs](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis)
