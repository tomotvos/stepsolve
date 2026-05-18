# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Build
dotnet build src/

# Run (http://localhost:5001)
dotnet run --project src/

# Run all tests
dotnet test tests/ -p:IsQuestionBuildEnabled=false

# Run a single test class
dotnet test tests/ -p:IsQuestionBuildEnabled=false --filter ClassName=StepSolveServiceTests

# Run a single test method
dotnet test tests/ -p:IsQuestionBuildEnabled=false --filter Name=SolveMode_CallsCameraAndSolver

# Publish for Raspberry Pi
dotnet publish src/ -c Release -r linux-arm64 --self-contained
```

Always pass `-p:IsQuestionBuildEnabled=false` to `dotnet test`.

## Architecture

Single-process Kestrel app. All components run in-process as singletons; no queues or separate workers.

**Main loop** — `StepSolveService` (BackgroundService) reads `StepSolve:Mode` from `IConfiguration` on every iteration (hot-reload safe). In `solve` mode it calls `ICameraCapture` → `ISolver` → `SolveState` → `OnStepClient`. In `demo` mode it skips the camera and picks a random image from `wwwroot/demo/`. In `calibrate` mode it captures continuously and broadcasts each frame to the dashboard (no solver call, no RA/Dec update); no-op on non-Linux. Broadcasts a status update whenever the mode changes.

**Solver routing** — `SolverRouter` (registered as `ISolver`) reads `Solver:Backend` at call time and delegates to `AstrometrySolver`, `Tetra3Solver`, or `CedarSolver`. No restart needed to switch backends.

**Python solvers** — `Tetra3Solver` and `CedarSolver` both extend `PythonSolverBase`, which manages a long-lived Python subprocess per solver. IPC is newline-delimited JSON over stdin/stdout:
- Startup: C# waits for `{"ready": true}` before the solver is considered available.
- Request: `{"image_path": "...", "ra_hint": null, "dec_hint": null, "radius_deg": null, "fov_estimate_deg": null}`
- Response: `{"ra_deg": 0.0, "dec_deg": 0.0, "confidence": 0.0, "sigma_used": 0, "attempts": 1, "error": null}`
- Subprocess access is serialized with a `SemaphoreSlim(1,1)`. On timeout or crash the process is killed and a 10 s backoff is set before re-launch.

**Validity** — `SolveResult.IsValid` returns `true` when `RaDeg != 0.0 || DecDeg != 0.0`. A failed solve returns `(0, 0, ...)` — exactly the north celestial pole is indistinguishable from failure.

**Configuration layering** (highest wins):
1. `appsettings.runtime.json` — written by `SettingsService` to `AppContext.BaseDirectory`; not in git
2. `appsettings.Development.json` — mac dev defaults (`Mode: idle`, local Python/venv paths)
3. `appsettings.json` — production defaults (`Mode: solve`, `Backend: astrometry`)

Config sections: `StepSolve`, `Solver`, `Camera`, `OnStep`. All use `IOptionsMonitor<T>` for hot-reload.

**WebSocket** — `WebSocketBroadcaster` fans out solve events, status changes, and log lines to all connected dashboard clients. `WebSocketLoggerProvider` hooks into the .NET logging pipeline so `StepSolve.*` Debug logs reach the dashboard.

**LX200** — `Lx200Server` is a second `BackgroundService` that binds port 5002 and handles `:GR#` (RA) and `:GD#` (Dec) commands for SkySafari. Slew commands are rejected.

## REST API

| Method | Path | Description |
|--------|------|-------------|
| GET | `/status` | Current solve result, mode, state, OnStep sync info |
| POST | `/mode` | Change mode; body `{"mode":"solve"}` or `?mode=solve` |
| GET | `/settings` | All configuration sections |
| POST | `/settings` | Validate, apply, and persist settings via `SettingsService` |
| POST | `/solve` | On-demand solve: no body → solve last captured image; `?demo=1` → fake result; multipart → solve uploaded file. Disabled in dashboard while in `solve` mode. |
| GET | `/solve/image` | Last captured or solved image (JPEG) |
| POST | `/system/shutdown` | `systemctl poweroff` after 1 s delay (Linux only; requires polkit rule) |
| POST | `/system/restart` | `systemctl reboot` after 1 s delay (Linux only; requires polkit rule) |
| GET | `/ws` | WebSocket — real-time solve/status/log stream |

## Key Files

```
src/
  Program.cs                    # DI wiring + all HTTP endpoints
  StepSolveService.cs           # Capture → solve → publish loop
  StepSolveOptions.cs           # All config POCOs (StepSolveOptions, SolverOptions, CameraOptions, OnStepOptions)
  SolveResult.cs                # SolveResult record + SolveHints; IsValid check
  SolveState.cs                 # Thread-safe last-result store
  Solvers/ISolver.cs            # Solver contract
  Solvers/PythonSolverBase.cs   # Python subprocess lifecycle + JSON IPC
  Solvers/SolverRouter.cs       # Backend dispatch at call time
  Solvers/SolverRegistration.cs # DI registration for all solver types
  Lx200Server.cs                # SkySafari TCP server (port 5002)
  OnStepClient.cs               # Mount sync (:Sr:/:Sd:/:CM#)
  SettingsService.cs            # Validation + persistence to appsettings.runtime.json
  WebSocketBroadcaster.cs       # Fan-out WebSocket events
  WebSocketLoggerProvider.cs    # Routes .NET logs to WebSocket
  wwwroot/                      # Dashboard (index.html + JS/CSS)
  wwwroot/demo/                 # Bundled JPEG images used by demo mode
  scripts/                      # Python solver service scripts (tetra3_solve_service.py, cedar_solve_service.py)
```
