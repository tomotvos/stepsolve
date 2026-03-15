# StepSolve Code Review (2026-03-15)

Scope:
- Reviewed PRD and rewrite plan against current implementation and tests.
- Focused on behavioral correctness, requirement coverage, and operational risk.

Validation run:
- `dotnet test` passed: 94/94 tests.

## Findings (ordered by severity)

1. High: OnStep safety threshold is effectively bypassed in normal flow
- In the solve loop, state is updated before OnStep sync is attempted, so the "previous" position used by the safety threshold check is already the new position.
- Evidence:
  - `StepSolveService` updates result, then calls sync.
  - `OnStepClient` reads previous coordinates from shared state for delta calculation.
- Impact:
  - Large erroneous jumps may not be blocked as intended.

2. High: Major PRD API surface is not implemented yet
- PRD requires endpoints not present in current server routes:
  - `GET /settings`
  - `POST /settings`
  - `POST /solve`
  - `GET /solve/image`
  - `POST /system/shutdown`
  - `POST /system/restart`
- Current implementation exposes only:
  - `GET /status`
  - `POST /mode`
  - `/ws`
  - static file fallback
- Impact:
  - Core operator workflows from PRD are unavailable.

3. Medium: Mode endpoint contract differs from PRD
- PRD documents `POST /mode` with JSON body (`{ "mode": "solve" }`).
- Implementation uses query string (`/mode?mode=...`) and frontend calls query form.
- Impact:
  - API clients built to PRD contract will fail.

4. Medium: Manual solve UX is incomplete
- Rewrite plan includes manual solve as a key UI interaction.
- UI includes a Solve Now button, but there is no click handler and no backend route for manual solve.
- Impact:
  - User-facing control appears but cannot perform the action.

5. Medium: Real-time log streaming is not integrated end-to-end
- PRD expects live logs over WebSocket.
- `WebSocketBroadcaster` has `BroadcastLog`, but there are no call sites wiring application logs to WebSocket output.
- Impact:
  - Dashboard log panel will not show live service logs as specified.

6. Medium: Multi-backend solver requirement not implemented
- PRD goal requires Astrometry, Cedar, and Tetra3.
- DI currently registers only Astrometry solver.
- `Solver:Backend` exists in config but is not used to select implementation.
- Impact:
  - Feature parity target is incomplete.

7. Low: Astrometry index path option appears unused
- `AstrometryOptions.IndexPath` is configurable but not consumed in solve-field argument construction.
- Impact:
  - Non-default index deployments may not work as expected.

## Positive observations

1. Architecture quality is clean and maintainable
- Single-process service model with clear component boundaries.
- Thread-safe shared state model and simple orchestration.

2. Baseline test suite is healthy for implemented scope
- Strong coverage for LX200 protocol formatting/handling, state model, OnStep math/protocol, Astrometry parsing, and WebSocket broadcast behavior.
- Gap is mostly requirement completeness rather than obvious regressions in implemented units.

## Recommended next steps

1. Fix OnStep safety logic first
- Compare against pre-update position (or last successful sync position) before writing new solve result into shared state.

2. Close API/PRD gap
- Implement `/settings`, `/solve`, `/solve/image`, `/system/shutdown`, `/system/restart`.
- Add integration tests for each route.

3. Align mode API contract
- Either accept PRD JSON body (preferred) or update PRD and keep query mode.
- Consider supporting both for compatibility.

4. Complete manual solve flow
- Add frontend handler and backend endpoint contract.

5. Wire structured logs to WebSocket stream
- Add a logging sink/bridge that forwards selected log events via `BroadcastLog`.

6. Implement backend solver selection
- Add Cedar and Tetra3 implementations and select by `Solver:Backend` at startup.
- Add backend selection tests.
