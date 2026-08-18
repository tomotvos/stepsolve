# OnStep calibration controller — implementation plan

## Purpose

Implement the reviewed calibration design in small, independently testable
phases. The end state is an operator-started, OnStep-controlled three-point
alignment workflow in StepSolve's **Calibrate** mode. It uses safe bounded
Alt/Az GoTos, plate solves each actual position, and adds the resulting points
to OnStepX's model.

The governing design is [ONSTEP_CALIBRATION_DESIGN.md](ONSTEP_CALIBRATION_DESIGN.md).

## Guardrails

- Do not change the existing normal Solve-mode behavior until the new
  controller and its safety gates are complete.
- A default startup probe and default validation mode must never command mount
  movement or synchronization.
- Only the calibration controller may issue mutating OnStep commands.
- The three-point sequence starts only from Calibrate mode after explicit
  operator confirmation that the rig is at home: north/level within ±5°.
- Never force-move energized stepper axes by hand during a session.
- All GoTos are fixed/configured targets inside an application-level envelope;
  never random targets.
- Every socket reply, eligibility decision, motion request, solve, acceptance,
  rejection, and abort is recorded in the dashboard log and exposed in status.

## Target behavior

```text
Operator sets Calibrate mode
          │
          ▼
OnStep probe (:GVP#, :GVN#, :GU#)
          │
          ▼
Operator confirms home (north/level ±5°) and starts alignment
          │
          ▼
:So90# → :A3# → target #1 (:Sa, :Sz, :MA#) → wait/settle → two matching solves
          │                                                  │
          │                                            Accept point
          │                                                  ▼
          └── repeat target #2 and #3 ─────────────→ :Sr, :Sd, :A+#
                                                             │
                                                             ▼
                                               Verify at fourth location
```

If a target does not solve, try a bounded alternate target (normally ±10°
azimuth) up to the configured retry count; then stop the session and require
operator action.

## Phase 0 — Baseline and test scaffolding

### Changes

1. Run the full current suite before changes:

   ```bash
   dotnet test tests/ -p:IsQuestionBuildEnabled=false
   ```

2. Add an `OnStepMockServer` test helper, preferably in a new
   `tests/OnStepMockServer.cs` file. It must:

   - accept a TCP client;
   - capture discrete `#`-terminated commands;
   - send queued replies, including delayed and malformed replies;
   - support assertion of exact command order; and
   - avoid tests depending on the physical controller.

3. Refactor only existing OnStep integration tests to use the helper where it
   improves readability. Do not change their asserted current behavior yet.

### Acceptance criteria

- Existing tests remain green.
- A test can deterministically simulate each protocol reply, timeout, and
  disconnect needed by later phases.

## Phase 1 — Typed OnStep protocol transport

### Files

- Modify `src/OnStepClient.cs`
- Add `src/OnStepProtocol.cs`
- Modify/add `tests/OnStepClientTests.cs`
- Add `tests/OnStepMockServer.cs`

### New types

```csharp
public sealed record OnStepIdentity(string Product, string Firmware);

public sealed record OnStepMountStatus(
    bool IsParked,
    bool IsParking,
    bool IsSlewing,
    bool IsHoming,
    bool IsGuiding,
    bool HasError,
    string Raw);

public sealed record OnStepPosition(double RaDeg, double DecDeg);

public sealed record OnStepCommandResult(
    bool Succeeded,
    string Command,
    string? Reply,
    string? Error);
```

The exact records may vary, but protocol details must not remain as anonymous
strings in the controller.

### Implementation

1. Add a private `SemaphoreSlim` to serialize every request/response exchange.
2. Replace write-only command helpers with a single internal operation:

   ```csharp
   Task<OnStepCommandResult> SendCommandAsync(
       string command,
       ReplyKind replyKind,
       CancellationToken ct);
   ```

   It creates one `TcpClient`, applies connect/read timeouts, writes the
   command, reads through `#`, validates the reply, and records it.

3. Add read-only APIs:

   - `ProbeAsync()` sends `:GVP#`, `:GVN#`, `:GU#`.
   - `GetStatusAsync()` sends and parses `:GU#`.
   - `GetPositionAsync()` sends and parses `:GR#`, `:GD#`.
   - `GetAlignmentProgressAsync()` sends `:A?#`.

4. Add mutation APIs, which are `internal` initially and callable only by the
   future controller:

   - `StartAlignmentAsync(3)` sends `:A3#`.
   - `GotoAltAzAsync(altitude, azimuth)` sends `:Sa`, `:Sz`, `:MA#` in order.
   - `AcceptAlignmentPointAsync(result)` sends `:Sr`, `:Sd`, `:A+#` in order.

5. Validate all set commands before continuing. A failed `:Sr` or `:Sd` must
   prevent `:A+#`; a failed altitude/azimuth target must prevent `:MA#`.

6. Keep the existing `SyncAsync` temporarily as a compatibility wrapper or
   remove it only when Phase 4 changes its caller.

### Tests

- correct command order and command formatting;
- success and error replies for `:Sr`, `:Sd`, `:Sa`, `:Sz`, `:MA`, `:CM`;
- timeout, disconnect, missing `#`, overlong reply, and malformed coordinate;
- no second command after first-command failure;
- serialization: two concurrent calls cannot interleave commands;
- `:GU#` parsing for parked, slewing, homing, guiding, normal, and unknown
  responses;
- `:GR#` / `:GD#` parsing and residual calculation.

### Acceptance criteria

- No caller can falsely report synchronization succeeded without parsing a
  successful reply.
- Protocol failures are structured, visible, and non-throwing to the solve
  loop.

## Phase 2 — Calibration domain model and read-only validation

### Files

- Add `src/OnStepCalibrationOptions.cs` or extend `StepSolveOptions.cs`
- Add `src/OnStepCalibrationState.cs`
- Add `src/OnStepCalibrationController.cs`
- Modify `src/Program.cs`
- Modify `src/SettingsService.cs`
- Modify `tests/StepSolveOptionsTests.cs`
- Add `tests/OnStepCalibrationControllerTests.cs`

### Configuration

Add and validate the following `OnStep` keys:

```text
StartupPolicy                    probe | wait-for-stable-solve | one-point-sync
BackgroundPolicy                 off | validate | conservative-sync
MinSolveConfidence               0.90
StableSolveIntervalSeconds       1
MaxSolveDisagreementDeg          0.05
MinCorrectionIntervalMinutes     15
MinCalibrationSeparationDeg      15
MaxAutomaticCorrectionDeg        2
MaxAutomaticSyncsPerHour         4
MaxAutomaticSyncsPerSession      8
CalibrationSettleSeconds         3
CalibrationTargetRetryCount      3
CalibrationTargets               ordered Az/Alt target list; default (0,45), (60,60), (90,80)
```

Use `probe` and `validate` as the defaults. Existing `SyncMode` is deprecated:
accept it for one release but log that it has no runtime effect. Do not infer a
new mutable policy from legacy configuration.

### Controller responsibilities

1. Own connection state, last status, last OnStep position, residual, last
   decision/reason, cooldown, and rate-limit counters.
2. Poll/probe OnStep after startup with the existing backoff semantics.
3. In `validate` policy, accept solve submissions but only calculate/display
   residuals; do not mutate OnStep.
4. Implement the shared candidate gate:

   - controller is connected and controller status is safe;
   - two valid solves separated in time agree within tolerance;
   - confidence meets threshold;
   - residual is known;
   - result lies in the configured calibration envelope;
   - cooldown, separation, and rate limits permit it.

5. Return a typed `CalibrationDecision` for every candidate, e.g.
   `Deferred`, `Rejected`, `ReadyForApproval`, `Accepted`, or `Failed`.

### API/status changes

Extend `/status`, `/settings`, and WebSocket `status` messages with a
`calibration` object. The first increment is read-only:

```json
{
  "connection": "ready",
  "controller": "On-Step",
  "firmware": "...",
  "mountState": "safe",
  "lastResidualDeg": 0.12,
  "lastDecision": "deferred",
  "lastDecisionReason": "waiting for a second stable solve",
  "nextEligibleAt": null,
  "alignment": "inactive"
}
```

### Tests

- each candidate gate independently and in combination;
- confidence, stability, residual, cooldown, separation, hourly, and session
  boundary conditions;
- safe state is required; unparseable status is unsafe;
- `validate` mode never issues a mutating protocol command;
- hot-reloaded options affect new candidates without restarting the service.

### Acceptance criteria

- With `OnStep:Enabled=true` and default new policies, a running service can
  identify and validate OnStep but cannot move or sync the mount.
- Dashboard/status state makes every non-action understandable.

## Phase 3 — Dashboard status and Calibrate-mode controls

### Files

- Modify `src/wwwroot/index.html`
- Modify `src/wwwroot/js/app.js`
- Modify `src/wwwroot/css/app.css`
- Modify `src/Program.cs`
- Add/modify `tests/ApiEndpointTests.cs`

### UX

1. Expand the existing OnStep card to show controller identity, connection,
   safe/unsafe state, residual, and last decision.
2. Show the calibration controls only while StepSolve mode is `calibrate`.
   Outside that mode, show status but no mount action buttons.
3. Add a two-step **Start 3-point alignment** confirmation dialog with the
   home precondition: true north and camera level within ±5°.
4. Add disabled-by-default **Accept point** and always-available **Abort**
   controls. The acceptance button only enables after a stable, safe candidate.
5. Add a compact session panel showing point number, requested Alt/Az,
   alternate attempt, GoTo state, settle countdown, solve values, residual,
   and OnStep replies.
6. Surface every state transition in the existing dashboard log.

### Endpoints

Add explicit, small commands rather than overloading `/mode` or `/settings`:

```text
POST /onstep/alignment/start
POST /onstep/alignment/accept
POST /onstep/alignment/abort
GET  /onstep/calibration
```

Every mutation endpoint verifies Calibrate mode, connection, safe status, and
the applicable state transition server-side. The browser is not trusted to
enforce safety.

### Tests

- endpoint rejection outside Calibrate mode;
- start requires confirmation payload and safe controller state;
- accept is invalid without a ready candidate;
- abort is idempotent and prevents subsequent client commands;
- status payload carries calibration state.

### Acceptance criteria

- An operator can see why the controls are disabled.
- Reloading the page reflects an active or failed session accurately.

## Phase 4 — Controlled three-point state machine

### Files

- Modify `src/OnStepCalibrationController.cs`
- Modify `src/StepSolveService.cs`
- Modify `src/OnStepClient.cs`
- Add/modify `tests/OnStepCalibrationControllerTests.cs`
- Add/modify `tests/StepSolveServiceTests.cs`

### State machine

```text
Inactive
  → Preflight
  → StartingAlignment
  → GotoPoint(n, attempt)
  → WaitingForGoto
  → Settling
  → AwaitingStableSolves
  → AwaitingAcceptance
  → AcceptingPoint
  → [next point or Completed]

Any state → Aborted | Failed
```

### Implementation details

1. `StartAlignmentAsync` confirms Calibrate mode, connection, safe status, and
   target plan before sending `:A3#`.
2. Validate every requested and alternate target against:

   - configured Alt/Az calibration envelope;
   - the V1 azimuth travel/cable envelope; and
   - OnStep's current status/limit response where available.

3. Command `:Sa`, `:Sz`, `:MA#`; poll `:GU#` until GoTo completes or an
   operation timeout expires. Check status before each poll and abort on any
   unsafe transition.
4. Wait `CalibrationSettleSeconds`, then ask `StepSolveService` for fresh
   frame solves. Do not use a stale last solve.
5. Require the Phase 2 stable-candidate gate before enabling acceptance. Each
   of the three points requires an explicit operator approval in the first
   release; the session never advances automatically from one candidate to
   the next.
6. On accept, submit the solved RA/Dec via `:Sr`, `:Sd`, `:A+#`; query
   alignment progress; record the accepted point.
7. On no solve, choose the next target alternate. Stop at the retry limit.
8. After point 3, query completion, show the session summary, and remain in
   Calibrate mode. Do not automatically begin periodic syncing.
9. `AbortAsync` cancels local timers and future commands. It must not issue a
   surprise movement or clear OnStep's model. The UI warns that OnStep may have
   an incomplete session.

### Tests

- exact happy-path command sequence for three points;
- waiting for status completion and settle timing;
- each alternate-target retry; no fourth attempt;
- reject target outside envelope before sending any movement command;
- abort during GoTo, settle, solve, and acceptance;
- protocol error/timeout at every state leaves controller safe and visible;
- no commands after service cancellation/restart;
- Calibrate mode without an active session retains its present capture-preview
  behavior.

### Acceptance criteria

- The only automated movements are the configured, in-envelope targets.
- A plate solve, not the requested target, is used to accept each model point.
- The session cannot silently run or continue after a safety failure.

## Phase 5 — Conservative one-point auto-sync (optional, after field data)

Do not implement this phase until the three-point workflow is field-tested.

### Implementation

1. Enable only when `BackgroundPolicy=conservative-sync`.
2. Reuse the Phase 2 candidate gate, including a fresh OnStep residual from
   `:GR#`/`:GD#`.
3. Require residual less than `MaxAutomaticCorrectionDeg` and all rate limits.
4. Call `SyncSolvedPositionAsync` only after the gate accepts the candidate.
5. Log and display both the pre-sync residual and OnStep reply.

### Acceptance criteria

- Default validation policy cannot accidentally transition to auto-sync.
- Large residuals require user review; they are never automatically corrected.

## Phase 6 — Field validation and release

### Bench validation

1. Use a mock server to exercise every test path.
2. With motors disconnected or motion disabled, verify UI state, command
   sequence, retries, and abort handling against the real controller.
3. Verify the application-level envelope rejects intentionally unsafe targets.

### Field sequence

1. Deploy with startup probe and validation only.
2. Verify controller identity/status and residual display.
3. At home (north/level within ±5°), run one controlled three-point session.
4. Verify a fourth independently selected location; record residuals.
5. Repeat across multiple sky geometries and test sites before changing
   thresholds or enabling conservative auto-sync.

### Release checklist

- Full `dotnet test tests/ -p:IsQuestionBuildEnabled=false` passes.
- Dashboard UI manually reviewed at desktop and Pi viewport sizes.
- Logs contain no credentials and present actionable errors.
- Deployment preserves `appsettings.runtime.json` and introduces safe defaults
  for existing users.
- Update `README.md`, `AGENTS.md`, and the configuration reference in
  `deploy/PI_SETUP.md` for any final setting names.

## Commit plan

Keep reviews small and bisectable:

1. `test: add OnStep protocol mock server`
2. `feat: add typed OnStep request-response client`
3. `feat: add read-only OnStep calibration validation`
4. `feat: add calibration status and dashboard controls`
5. `feat: add controlled three-point OnStep alignment`
6. `feat: add conservative one-point sync policy` (only after field evidence)
7. `docs: document OnStep calibration workflow`

## Decisions needed before Phase 4

1. Verify that the selected initial targets `(Az 0°, Alt 45°)`,
   `(Az 60°, Alt 60°)`, and `(Az 90°, Alt 80°)` and their alternate azimuth
   direction respect the physical cable path and local tree line.
2. Confirm whether StepSolve should use a fixed calibration envelope in
   addition to OnStep limits, and provide its initial bounds.
3. Confirm the maximum acceptable first-target error from home before the
   session stops rather than tries an alternate target.
4. Keep acceptance of every stable point operator-approved for the first
   field release. Reconsider automatic acceptance only after field evidence.
