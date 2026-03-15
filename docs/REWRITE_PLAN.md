# StepSolve Rewrite Plan

> Captured from architecture review session, March 14 2026.
> This document records the decisions made and serves as the execution plan for the rewrite.

---

## 1. Decision: Start Fresh

### Why not continue the Python codebase?

The existing `skysolve_next/` Python implementation (~1,670 lines, 52 tests) was reviewed in detail. Findings:

- **Broken abstractions**: The `Solver` ABC declares `solve(image: ndarray)` but the only real implementation (`AstrometrySolver`) takes `image_path: str` with different kwargs. The interface contract is violated by its own implementation.
- **No separation of concerns**: `app.py` (373 lines) mixes routing, global state, file I/O, solve logic, and settings serialization. `solve_worker.py` (339 lines) embeds `CameraCapture`, the main loop, status writing, and init logic in one file.
- **Duplicated code**: `write_status()` implemented identically in both `app.py` and `solve_worker.py`. `_format_ra()` / `_format_dec()` copy-pasted across three modules.
- **Fragile state management**: Web app and worker communicate via JSON files on disk with no locking. Settings mutation uses `setattr` on pydantic models with hardcoded relative paths.
- **Test suite tests the wrong things**: No `conftest.py`, tests write to real `settings.json`, `test_log_parsing.py` tests a copy-pasted function not the actual source, placeholder/empty test files.
- **Frontend**: 762-line monolithic HTML with inline JS/CSS, XSS vulnerability via `innerHTML`, Tailwind CDN, dead code paths.
- **Environment mismatch**: Instructions say Python 3.11, venv is actually 3.13, generating 30K+ deprecation warnings per test run.
- **Over-engineered logging**: 250+ lines of custom formatters, capture handlers, file monitors — yet uses deprecated `datetime.utcnow()`.

**At ~1,670 lines, rewriting cleanly is faster than refactoring.**

### What to preserve

| Asset | Why |
|---|---|
| LX200 protocol handling | Well-tested, handles SkySafari quirks (timeouts, precision toggle, set commands) |
| Astrometry `solve-field` command construction & output parsing | Working regex patterns, 2-phase solve strategy (hints → fallback) |
| OnStep `:Sr/:Sd/:CM#` protocol | Simple, correct |
| PRD (`docs/skysolve_prd_v7_consolidated.md`) | Good requirements — don't re-derive |
| API docs (`docs/api.md`) | Reference for endpoints |
| Gap analysis (`docs/skysolve_gap_analysis_2025-09-02.md`) | Tracks what's missing |

---

## 2. Decision: .NET Runtime

### Why change from Python?

The goal is a solid **appliance** — predictable startup, no dependency drift, resilience to power-off, years of unattended operation. Python's venv/pip model is fragile for this: venvs break, pip resolves differently over time, version drift between dev and deploy.

### Why .NET over Go?

- Familiar runtime — 3-5x faster development velocity vs learning Go from scratch
- `dotnet publish -r linux-arm64 --self-contained -p:PublishAot=true` → single ~15MB binary
- Official ARM64 support since .NET 8, production-ready AOT. Starting with .NET 9 (SDK 9.0.305); will upgrade to .NET 10 LTS when convenient.
- `BackgroundService` pattern replaces the entire separate worker process
- Kestrel is a production-grade web server built in (replaces FastAPI + Uvicorn)
- Strong typing, mature IDE, refactoring tools

### Runtime impact on Pi

Negligible. The bottleneck is `solve-field` (2-30 seconds per solve), not the runtime:

| | Idle RAM | Under load |
|---|---|---|
| Python (current) | ~50-80 MB | ~120-180 MB |
| .NET 9 AOT | ~15-25 MB | ~30-50 MB |

Startup: ~200ms for AOT. Fine for a systemd service that starts once and runs for months.

### Camera interface

**No Picamera2 needed.** Shell out to `rpicam-still` (CLI tool available on all Pi OS versions). Any language can do this. Same approach already used for `solve-field`.

---

## 3. Architecture: Single-Process Headless Service

### Core insight

The UI is a diagnostic dashboard, not the primary interface. The product is a headless service that:

1. **Captures** an image (`rpicam-still`)
2. **Solves** it (`solve-field`)
3. **Publishes** RA/Dec over LX200 TCP for SkySafari
4. **Syncs** to OnStepX (optional, via `:Sr/:Sd/:CM#`)

Running continuously, unattended, from boot. The web UI is just a "how's it going?" window on your phone.

### Process model

```
┌─────────────────────────────────────┐
│          StepSolveService           │
│                                     │
│  loop:                              │
│    capture image (rpicam-still)     │
│    solve image (solve-field)        │
│    if solved:                       │
│      update in-memory state         │
│      publish to LX200 listeners     │
│      sync to OnStepX if enabled     │
│    sleep until next capture         │
│                                     │
│  LX200 TCP server (port 5002)       │
│    responds to :GR# :GD# queries   │
│    from current in-memory state     │
│                                     │
│  OnStep TCP client (port 9998)      │
│    pushes :Sr :Sd :CM# on solve     │
│                                     │
│  HTTP server (port 5001)            │
│    GET /status → current RA/Dec     │
│    GET / → diagnostic page          │
│    WebSocket → log stream           │
└─────────────────────────────────────┘
```

**One process. One binary. One systemd unit.** No worker/web split, no file-based IPC.

The `SolveWorker` is a `BackgroundService` running inside the same ASP.NET host process. It writes to in-memory state that the LX200 server and HTTP endpoints read. Thread-safe with a simple `lock` or `Channel<T>`.

---

## 4. Project Structure

**Project name: StepSolve** — repo `stepsolve`, binary `stepsolve`, hostname `stepsolve.local`, service `stepsolve.service`.

**Repository:** New repo (`stepsolve`), separate from `skysolve-next`.

```
<project>/
├── src/
│   ├── Program.cs                  # Host setup, minimal API endpoints (~60 lines)
│   ├── StepSolveService.cs         # BackgroundService: capture→solve→publish loop
│   ├── Lx200Server.cs              # TCP listener for SkySafari
│   ├── OnStepClient.cs             # TCP client for mount sync
│   ├── Solvers/
│   │   ├── ISolver.cs              # Interface: Task<SolveResult> SolveAsync(string imagePath, SolveHints? hints)
│   │   ├── AstrometrySolver.cs     # Shell out to solve-field (one process per solve — solve-field is slow anyway)
│   │   └── CedarSolver.cs          # Communicates with long-running cedar_solve_service.py via stdin/stdout pipe
│   ├── CameraCapture.cs            # Shell out to rpicam-still
│   ├── StepSolveOptions.cs         # Strongly-typed config from appsettings.json
│   ├── SolveResult.cs              # record struct
│   └── wwwroot/
│       ├── index.html              # Diagnostic dashboard
│       ├── css/
│       │   └── app.css             # Day/night theme, responsive
│       └── js/
│           ├── app.js              # Init, status display
│           ├── api.js              # Fetch wrapper
│           └── logs.js             # WebSocket log stream
├── solvers/
│   ├── cedar_solve_service.py      # Long-running process: reads requests from stdin, writes JSON results to stdout
│   ├── tetra3_solve_service.py     # Same pattern for tetra3
│   └── requirements.txt           # cedar-solve==0.5.1, tetra3
├── deploy/
│   ├── stepsolve.service           # systemd unit
│   ├── install.sh                  # Pi install script (incl. solver venv setup)
│   └── avahi/                      # mDNS service files
├── hotspot/
│   └── (autohotspot scripts carried forward from legacy)
└── docs/
    └── (requirements, PRD, API docs — carried from skysolve-next)
```

~10 C# files, ~800-1000 lines total. ~200 lines of HTML/JS for the dashboard.
~100 lines of Python total (solver service wrappers — long-running processes, not per-invoke scripts).

The Python solver wrappers use a **long-running pipe** pattern to avoid per-solve startup overhead:
- .NET starts `python cedar_solve_service.py` once at service startup
- The Python process loads cedar-solve and its database into memory (one-time cost)
- Per solve: .NET writes a JSON request to stdin, reads a JSON response from stdout (~1-2ms overhead)
- This preserves cedar's ~10ms solve speed (total ~12ms) instead of the ~210ms that a per-invoke subprocess would cost
- No changes to cedar-solve or tetra3 — they're used as standard pip-installed libraries

---

## 5. UI Approach

### Static HTML + vanilla JS, served by Kestrel

No Blazor, no Razor Pages, no frontend framework, no npm, no build step.

**Rationale:**
- The UI has ~5 interactions: show last solve, trigger manual solve, change mode, edit settings, stream logs
- That's vanilla JS territory — a framework adds complexity with zero benefit
- Files get embedded in the binary via `app.UseStaticFiles()`
- No Node.js toolchain needed

**Key improvement over current Python version:**
- Separate `.js` files instead of 400 lines inline
- Sanitized output (`textContent` not `innerHTML` — fixes XSS)
- CSS custom properties for day/night theme
- Single WebSocket for all real-time data (replaces three polling intervals):

```javascript
const ws = new WebSocket(`ws://${location.host}/ws`);
ws.onmessage = (e) => {
    const msg = JSON.parse(e.data);
    switch (msg.type) {
        case 'solve':  updateSolveResult(msg); break;
        case 'status': updateWorkerStatus(msg); break;
        case 'log':    appendLogEntry(msg); break;
    }
};
```

---

## 6. Deployment Model

### Build (on Mac)

```bash
dotnet publish -c Release -r linux-arm64 --self-contained -p:PublishAot=true
```

### Deploy (to Pi)

```bash
scp bin/Release/net9.0/linux-arm64/publish/stepsolve pi@stepsolve.local:/usr/local/bin/
ssh pi@stepsolve.local "sudo systemctl restart stepsolve"
```

### Systemd unit

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

[Install]
WantedBy=multi-user.target
```

### Appliance behavior

- `systemctl enable stepsolve` → starts on boot
- LX200 server listens → SkySafari finds it via mDNS (Avahi)
- Solve loop runs continuously → coordinates stay current
- Phone check: `http://stepsolve.local:5001` → see RA/Dec, confidence, last solve time
- Power cut → Pi reboots → service starts → solving resumes
- No user interaction needed for normal operation

---

## 7. Key Technical Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Runtime | .NET 9 AOT (upgrade to .NET 10 LTS later) | Single binary, familiar, ARM64 production-ready |
| Web framework | ASP.NET Minimal APIs | 11 simple endpoints, no MVC overhead |
| Background work | `BackgroundService` | In-process, no IPC needed |
| LX200 server | Raw `TcpListener` + `Task.Run` per client | Same thread-per-client as Python version, proven pattern |
| Camera | Shell out to `rpicam-still` | No Picamera2 dependency, works from any language |
| Astrometry solver | Shell out to `solve-field` | Already a subprocess in Python version, C binary |
| Cedar/Tetra3 solver | Long-running Python process via stdin/stdout pipe | Cedar-solve and tetra3 are Python-only; process starts once, database loads once, per-solve overhead ~1-2ms. No changes to cedar/tetra3 needed. |
| Config | `appsettings.json` + `IOptions<T>` | Strongly typed, hot-reload built in |
| Frontend | Static HTML/JS/CSS | No framework, no build step, embedded in binary |
| Real-time | Single WebSocket | Replaces polling for status, solves, and logs |
| State sharing | In-memory with `lock` | No file-based IPC between components |
| Deployment | `scp` single binary + solver venv | .NET binary is self-contained; Python venv only for Cedar/Tetra3 (long-running, not per-invoke) |
| Networking | Autohotspot scripts (carried forward) | Pi acts as AP in the field, client at home; .NET service binds `0.0.0.0`, doesn't care which mode |

---

## 8. Execution Phases

### Phase 1: Foundation
- [ ] Create .NET 9 solution and project
- [ ] `Program.cs` with minimal API host and static files
- [ ] `StepSolveOptions.cs` with config binding
- [ ] `SolveResult.cs` record
- [ ] Verify `dotnet publish` AOT for linux-arm64 works from Mac
- [ ] Basic systemd unit file

### Phase 2: Core Solve Loop
- [ ] `CameraCapture.cs` — shell out to `rpicam-still` (mock on macOS)
- [ ] `ISolver.cs` interface with `Task<SolveResult> SolveAsync(string imagePath, SolveHints? hints)`
- [ ] `AstrometrySolver.cs` — port solve-field invocation and output parsing
- [ ] `CedarSolver.cs` — manages long-running `cedar_solve_service.py` process via stdin/stdout pipe
- [ ] `cedar_solve_service.py` / `tetra3_solve_service.py` — long-running Python wrappers (~50 lines each); load solver+database once, read JSON requests from stdin, write JSON results to stdout
- [ ] Solver venv setup in install script (`pip install cedar-solve tetra3` into isolated venv)
- [ ] 2-phase solve strategy (hints → unhinted fallback)
- [ ] Config-driven solver selection (astrometry, cedar, tetra3)
- [ ] `StepSolveService.cs` BackgroundService with capture→solve loop
- [ ] In-memory state with thread-safe access

### Phase 3: LX200 Server (SkySafari)
- [ ] `Lx200Server.cs` — TCP listener on port 5002
- [ ] Handle `:GR#`, `:GD#` from in-memory state
- [ ] Handle `:GVP#`, `:GVN#`, `:U#` and set commands (ACK only)
- [ ] Ignore motion commands
- [ ] Test with SkySafari

### Phase 4: OnStep Client
- [ ] `OnStepClient.cs` — TCP client to OnStepX on port 9998
- [ ] `:Sr`, `:Sd`, `:CM#` sync on successful solve
- [ ] Configurable enable/disable
- [ ] Retry logic with backoff
- [ ] Safety threshold (`SYNC_MAX_DEG`)

### Phase 5: Diagnostic Dashboard
- [ ] `wwwroot/index.html` — status display, mode switch, settings
- [ ] WebSocket endpoint for real-time solve results and logs
- [ ] Day/night theme toggle
- [ ] Log stream with pause/clear

### Phase 6: Settings & Image Preview
- [ ] Settings persistence — `POST /settings` writes changes to `appsettings.json` on disk so they survive restarts
- [ ] Dashboard settings panel — expandable sections for Solver, Camera, and OnStep configuration with save button
- [ ] Settings validation — reject invalid values (e.g. negative shutter, invalid backend name) before persisting
- [ ] Image preview section on dashboard — display last captured/solved image via `/solve/image`
- [ ] Image preview updates via WebSocket `solve` messages — dashboard fetches new image after each solve
- [ ] Copy demo images from `skysolve_legacy/` into `wwwroot/demo/` for macOS mock captures
- [ ] `CameraCapture` on macOS returns a random demo image instead of a blank mock frame
- [ ] Image thumbnail in solve result display — visible confirmation that a real frame was captured

### Phase 7: Networking & Hotspot
- [ ] Carry forward autohotspot scripts from legacy (NetworkManager AP mode)
- [ ] Verify mDNS/Avahi works in both AP mode (field) and client mode (home)
- [ ] Service binds `0.0.0.0` — works regardless of network mode
- [ ] Document hotspot setup and switching

### Phase 8: Deployment
- [ ] Avahi/mDNS service files for `_http._tcp` and `_lx200._tcp`
- [ ] Install script for Pi (binary + solver venv + systemd + avahi + hotspot)
- [ ] Integration test with real hardware (Pi + camera + SkySafari + OnStepX)
- [ ] Test Cedar/Tetra3 solve speed vs Astrometry on Pi
- [ ] Documentation update

---

## 9. What Stays in This Repository (`skysolve-next`)

This repo is archived. Useful assets are copied to the new repo:

- `docs/` — PRD, API docs, gap analysis, this rewrite plan
- `skysolve_legacy/` — reference implementation for protocol logic
- Autohotspot scripts — carried forward for AP mode

The existing `skysolve_next/` Python code is not developed further.

---

## 10. Resolved Questions

- [x] **New repo or subdirectory?** → New repo. Clean separation from the Python project.
- [x] **.NET 8 or .NET 9?** → Starting with .NET 9 (SDK 9.0.305 installed). Upgrade to .NET 10 LTS when convenient — code is identical, only TFM changes.
- [x] **mDNS/Avahi setup?** → Avahi is an OS-level concern, not a .NET concern. The service binds `0.0.0.0` and works in both AP mode (field, Pi is hotspot) and client mode (home, Pi joins WiFi). Autohotspot scripts from the legacy project handle network switching.
- [x] **Cedar/Tetra3 solver support?** → Python-only packages (no CLI). Solution: long-running Python process with stdin/stdout pipe (~50 lines each) in an isolated venv on the Pi. The .NET app starts `python cedar_solve_service.py` once at startup and communicates via JSON over pipes — per-solve overhead is ~1-2ms, preserving cedar's ~10ms solve speed. No changes to cedar-solve or tetra3 required. If cedar's Rust library ever exposes a C ABI, can upgrade to P/Invoke for ~0ms overhead.
- [x] **AOT cross-compilation from Mac?** → Not possible natively — macOS lacks the Linux cross-linker toolchain (`ld.bfd`) required by .NET Native AOT. Current approach: self-contained publish from Mac (~110 MB folder, works fine). AOT builds (~15 MB single binary) should use one of: (a) build natively on Pi, (b) Docker cross-build, or (c) GitHub Actions CI with linux-arm64 runner. The `.csproj` conditionally enables AOT only when targeting Linux RIDs.

---

## 11. Solver Index Management

All solver backends use index/database files. The location must be configurable and swappable without rebuilding or restarting the service.

### Config model

```json
{
  "Solver": {
    "Backend": "cedar",
    "IndexPath": "/var/lib/stepsolve/indexes/cedar-default",
    "Astrometry": {
      "SolveFieldPath": "solve-field",
      "IndexPath": "/usr/share/astrometry"
    },
    "Cedar": {
      "IndexPath": "/var/lib/stepsolve/indexes/cedar-default"
    },
    "Tetra3": {
      "IndexPath": "/var/lib/stepsolve/indexes/tetra3-default"
    }
  }
}
```

### Design principles

- **Configurable path per backend.** Each solver reads its index path from config. No hardcoded paths.
- **Hot-swappable.** Change `IndexPath` in `appsettings.json` → service picks it up on next solve cycle (via `IOptionsMonitor<T>` hot reload). No restart required.
- **Multiple index sets.** Keep different index sets in separate directories (e.g., `cedar-wide-field`, `cedar-narrow`). Switch by changing the config path.
- **CLI wrappers receive the path as a field in the JSON request over stdin.** No command-line arguments per solve:
  ```json
  {"image_path": "/var/lib/stepsolve/images/capture.jpg", "index_path": "/var/lib/stepsolve/indexes/cedar-default"}
  ```
- **Astrometry.net uses its own config mechanism** (`--config` flag or `ASTROMETRY_INDEX_DIR` env var), but we pass it explicitly for consistency.
- **Index files are not shipped with the app.** The install script downloads them separately. Different Pi setups may use different index sets depending on optics.

### Directory layout on Pi

```
/var/lib/stepsolve/
├── indexes/
│   ├── astrometry/           # Astrometry.net 4100/4200 series
│   ├── cedar-default/        # Cedar default database
│   ├── cedar-narrow/         # Cedar narrow-field (optional)
│   └── tetra3-default/       # Tetra3 default database
├── images/                   # Captured/solved images (runtime)
└── logs/                     # Log files (if file logging enabled)
```

---

## 12. Remaining Open Questions

None — all questions resolved.
