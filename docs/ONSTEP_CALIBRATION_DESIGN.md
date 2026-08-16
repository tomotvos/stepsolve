# OnStep calibration controller — design and implementation plan

## Status and objective

The first StepSolve/OnStepX field test was successful: StepSolve solved the
rig's pointing correctly and OnStepX accepted the resulting synchronization.
The remaining work is to turn that useful proof of integration into a
deliberate, conservative calibration system.

The objective is **not** to continually overwrite OnStep's coordinates. It is
to:

1. detect and describe an OnStepX connection at startup;
2. validate pointing regularly without changing it by default;
3. make a small, well-vetted one-point correction only when explicitly
   configured; and
4. support a user-supervised, OnStep-controlled three-point alignment that
   builds an OnStepX pointing model.

This document is a design proposal only. It deliberately does not change
StepSolve behavior.

## What the current implementation does

`OnStepClient.SyncAsync()` currently sends this sequence after **every** valid
solve while `OnStep:Enabled` is true:

```text
:SrHH:MM:SS#       set target RA
:Sd+DD*MM:SS#      set target Dec
:CM#               synchronize
```

It does not wait for, parse, or report protocol replies. The solve loop calls
it fire-and-forget, so consecutive successful solves can overlap. The existing
`MaxSyncDeltaDeg` check compares a solve to the last accepted StepSolve solve;
that suppresses normal calibrations at a new sky location rather than
measuring the correction being made.

`OnStep:SyncMode` is currently validated but is not used by `OnStepClient`.

These are reasonable prototype choices, but are not sufficient for unattended
calibration.

## OnStepX protocol model

### Connection and safety queries

At startup and before a calibration action, StepSolve should use:

```text
:GVP#              product name; confirms an OnStep-compatible controller
:GVN#              firmware version
:GU#               textual mount status
:GR# / :GD#        current reported RA / Dec for residual calculation
```

`#` terminates every reply. StepSolve must use bounded read timeouts and retain
the raw reply in its diagnostic log.

`:GU#` is the action gate. At minimum, calibration must be rejected if the
mount is parked, parking, homing, slewing, guiding, or reports a general
error. It should also reject unknown/unparseable status rather than assuming
it is safe.

### One-point synchronization

Outside an alignment session, the existing `:Sr`, `:Sd`, `:CM#` sequence is a
one-point synchronization. It is useful for a small correction after a stable
solve, but it is not the mechanism for accumulating a multi-point pointing
model.

The client must wait for the success response to each set-target command and
for the final `:CM#` response. A failed, incomplete, or malformed reply makes
the entire operation unsuccessful; no later command is sent.

### Three-point model building

OnStepX has a manual `n`-star alignment state machine:

```text
:A3#               begin a new three-star alignment
:Sr...# :Sd...# :CM#  accept first solved point
:Sr...# :Sd...# :CM#  accept second solved point
:Sr...# :Sd...# :CM#  accept third solved point and build the model
```

While this alignment state is active, OnStepX treats `:CM#` as "add the
current alignment point." Consequently, StepSolve can supply the solved
RA/Dec and accept the point without requiring a named star or manual centring.

Starting `:A3#` resets OnStep's home/alignment basis. The session therefore
has a non-negotiable precondition: the operator must put the rig at its known
home/park reference and explicitly start the session. The system must never
begin `:A3#` automatically on service startup.

For the V1 reference rig, the operational home tolerance is deliberately
modest: align azimuth to true north and camera altitude to horizontal within a
few degrees (target: ±5°). It is a safe starting reference for the first
bounded GoTo, not the final pointing calibration; the first plate-solved point
absorbs the remaining home offset.

For the Alt-Az reference rig, StepSolve should use OnStepX's horizontal-target
commands rather than asking the operator to back-drive energized steppers:

```text
:Sa+DD*MM#         set target altitude
:SzDDD*MM#         set target azimuth
:MS#               slew to the target
```

After the controller reports that the GoTo is complete and the rig has settled,
StepSolve plate-solves the actual pointing, replaces the target with that
solved RA/Dec using `:Sr`/`:Sd`, and sends `:CM#` to accept the alignment
point. This deliberately makes the solved pointing—not the requested target—
the alignment truth.

## Proposed operating policies

### Startup policy

| Policy | Action | Default |
|---|---|---:|
| `probe` | Connect, identify controller, fetch status; change nothing. | yes |
| `wait-for-stable-solve` | Probe, then report the first safe candidate but change nothing. | no |
| `one-point-sync` | Probe, then perform one gated correction after a stable solve. | no |

An unavailable controller is a status condition, not a failure of the solver.
Retry in the background using the existing capped backoff. Do not issue any
mount motion command while retrying.

### Background policy

| Policy | Behavior | Default |
|---|---|---:|
| `off` | No OnStep action after the startup probe. | no |
| `validate` | Measure and display residuals only. | yes when OnStep is enabled |
| `conservative-sync` | Perform gated one-point syncs. | no |

The assisted three-point workflow lives in StepSolve's existing **Calibrate**
mode, not as a background policy.

## Calibration eligibility gate

A solve becomes a *candidate* only if all conditions are true:

1. OnStep was identified and its latest `:GU#` status is safe.
2. The mount is stationary. Require two successful solves at least several
   seconds apart that agree within a configurable angular tolerance.
3. Each solve satisfies a configurable confidence threshold.
4. The solved position is away from the horizon and the Alt-Az zenith region.
   Exact limits should be configurable and initially match OnStep's own
   limits.
5. The residual between OnStep's `:GR#`/`:GD#` position and the solved
   position is smaller than `MaxCorrectionDeg`.
6. The cooldown since the last accepted sync has elapsed.
7. The result is sufficiently separated from the last accepted point, unless
   the user explicitly invokes a one-point correction.
8. The hourly and session rate limits have not been reached.

The candidate is rejected, logged, and shown in the dashboard with its reason
when any gate fails. A rejected solve never changes OnStep state.

Suggested safe starting values, to tune from field data:

| Setting | Initial value | Reason |
|---|---:|---|
| Stable-solve spacing | 5 s | avoids accepting a frame during movement |
| Solve agreement | 0.05° (3 arcmin) | rejects transient or bad solves |
| Minimum correction interval | 15 min | prevents rapid model churn |
| Minimum sky separation | 15° | avoids repeat corrections at one location |
| Maximum automatic correction | 2° | anything larger merits review |
| Maximum auto-syncs/hour | 4 | hard upper bound during a fault |
| Maximum auto-syncs/session | 8 | stops unattended accumulation |

The residual test replaces the current `MaxSyncDeltaDeg` behavior. A new sky
position should not be rejected merely because it is far from the preceding
calibration point.

## Calibrate mode and the assisted three-point workflow

The present Calibrate mode captures and broadcasts frames continuously; it
does not invoke the solver. Preserve that camera/framing behavior when no
OnStep alignment session is active. When an operator starts an OnStep
calibration session from Calibrate mode, the session controller additionally
requests solves from the fresh captured frames. This puts camera tuning,
solver tuning, and mount-model tuning in one deliberate workspace without
making ordinary camera calibration send mount commands.

This is the recommended first mount-calibration feature.

1. The dashboard explains prerequisites: rig physically at known home/park
   reference (true north / camera level within approximately ±5°), safe sky,
   tracking enabled as appropriate, and no other mount controller taking
   control.
2. The operator clicks **Start assisted 3-point alignment**. StepSolve probes
   OnStep, verifies safe status, asks for a final confirmation, then sends
   `:A3#`.
3. StepSolve selects point 1 from a fixed, configured safe target plan. It
   sends `:Sa`, `:Sz`, `:MS#`, waits for OnStep to report no active GoTo, then
   waits for a configurable settle period.
4. StepSolve obtains two stable solves, displays the candidate RA/Dec and
   residual, then offers **Accept point 1**. Acceptance sends `:Sr`, `:Sd`,
   `:CM#` serially and records the replies.
5. Repeat the controlled GoTo/settle/solve/accept sequence for points 2 and
   3. The controller's alignment progress is queried after every acceptance.
6. On completion, show the three accepted positions, their residuals, OnStep
   firmware/status, and a recommendation to verify with an independent solve
   after moving to a fourth location.
7. The operator may abort at any time. StepSolve stops sending commands and
   reports that the OnStep session may be incomplete; it must not attempt to
   silently repair or clear it.

If a point does not solve—because of a tree, cloud, or sparse/star-poor view—
the controller tries the next configured alternate, normally a 10° azimuth
offset at the same altitude. It must stop after a small configurable number of
attempts (initially three) and return control to the operator rather than
wandering the rig around the sky.

For this Alt-Az reference rig, suggested point geometry is deliberate rather
than random:

- choose altitudes away from the horizon and zenith;
- use at least 60° of azimuth separation, preferably about 80–100°;
- give the third point a distinctly different altitude and azimuth;
- never near the horizon, zenith, cable limits, or the ±150° V1 azimuth stop.

The initial target plan is fixed and reviewable: `(Az 0°, Alt 45°)`,
`(Az 60°, Alt 60°)`, and `(Az 90°, Alt 80°)`, all relative to the established
0,0 home reference. The exact values must be validated against the rig's
software limits and cable envelope before use.

Do not use "altitude = site latitude" as the first target: at azimuth north it
is near the celestial pole, where the geometry is weak and OnStep's own manual
alignment guidance advises against a target near the NCP/SCP.

Controlled, bounded GoTos are in scope for phase one. Random GoTos remain out
of scope. The application must check both OnStep-reported limits and an
application-level calibration envelope before every motion command.

## Configuration design

Keep the existing connection settings and add explicit policies:

```json
"OnStep": {
  "Enabled": false,
  "Host": "localhost",
  "Port": 9998,
  "StartupPolicy": "probe",
  "BackgroundPolicy": "validate",
  "MinSolveConfidence": 0.90,
  "StableSolveIntervalSeconds": 5,
  "MaxSolveDisagreementDeg": 0.05,
  "MinCorrectionIntervalMinutes": 15,
  "MinCalibrationSeparationDeg": 15,
  "MaxAutomaticCorrectionDeg": 2.0,
  "MaxAutomaticSyncsPerHour": 4,
  "MaxAutomaticSyncsPerSession": 8,
  "CalibrationSettleSeconds": 5,
  "CalibrationTargetRetryCount": 3,
  "CalibrationTargets": [
    { "azimuthDeg": 0, "altitudeDeg": 45 },
    { "azimuthDeg": 60, "altitudeDeg": 60 },
    { "azimuthDeg": 90, "altitudeDeg": 80 }
  ]
}
```

`SyncMode` should be deprecated. It currently has no runtime effect and its
`align` value is too ambiguous: a one-point sync is not a multi-point
alignment. On upgrade, retain the property for one release, log a warning when
present, and map no behavior from it. Missing new policies use the safe
`probe`/`validate` defaults.

## Software design

### New components

`OnStepClient` remains the protocol transport and gains request/response
methods:

- `ProbeAsync()` — product, firmware, status;
- `GetPositionAsync()` — parse `:GR#` and `:GD#`;
- `GetStatusAsync()` — parse `:GU#` into a typed status record;
- `SyncAsync()` — serial `:Sr`, `:Sd`, `:CM#` with reply validation;
- `StartAlignmentAsync(stars)` and `GetAlignmentProgressAsync()`.

All socket use is serialized with one `SemaphoreSlim`; no calibration command
may overlap another. Each operation gets a connect timeout, read timeout, and
structured result containing raw replies for diagnostics.

`OnStepCalibrationController` is a new singleton that owns policy, candidate
state, cooldowns, rate limits, and the assisted alignment state machine. It is
the only component allowed to call mutation methods on `OnStepClient`.

`StepSolveService` submits valid solves to the controller instead of directly
calling `SyncAsync`. In ordinary Solve mode it never awaits a long calibration
operation in the capture/solve loop. In Calibrate mode it supplies newly
captured frames to the active calibration session, which alone decides whether
to invoke the solver and when to command the next target.

### State and status

Expose a typed calibration state in `/status` and WebSocket status messages:

```text
connection: disconnected | probing | ready | incompatible | error
policy: probe | validate | conservative-sync
lastResidualDeg
lastDecision: accepted | deferred | rejected | failed
lastDecisionReason
nextEligibleAt
sessionSyncCount
alignment: inactive | awaiting-point-1 | awaiting-point-2 |
           awaiting-point-3 | completed | aborted | failed
```

The dashboard must distinguish **connected**, **safe to calibrate**, and
**calibrated**. These are not equivalent states.

## Dashboard design

Add an OnStep Calibration panel:

- controller name, firmware, host, current mount status;
- policy selectors and guarded numeric settings;
- last solved versus OnStep RA/Dec and angular residual;
- reason a candidate was rejected or deferred;
- **Start assisted 3-point alignment**, **Accept point**, and **Abort**;
- a compact session history of accepted points and replies.

Starting or accepting an alignment point is an explicit two-step confirmation.
The start button remains disabled while OnStep reports unsafe motion/state.
During an active session, show the requested Alt/Az, current point/attempt,
GoTo and settling progress, last frame/solve result, and the next alternate
target if a solve fails.

## Tests

### Unit tests

- parse successful, malformed, timed-out, and error replies;
- command order and "stop on first failed reply" behavior;
- all eligibility gates and boundary values;
- cooldown, hourly/session limits, and solve-stability windows;
- residual calculation using parsed OnStep coordinates;
- status parsing, especially parked/slewing/guiding/homing/error rejection;
- `:A3#` session transitions, acceptance, completion, and abort.
- direct `:Sa`/`:Sz`/`:MS#` target sequencing, completion polling, and settle
  timing;
- target-envelope rejection, bounded alternate retries, and an abort that
  sends no further motion commands.

### Protocol integration tests

Extend the mock OnStep TCP server to script replies and assert:

- `:GVP#`, `:GVN#`, `:GU#` startup probing;
- normal `:CM#` single-point sync;
- a three-point controlled-GoTo sequence with no overlapping socket commands;
- an error or timeout leaves the controller in a visible safe state;
- an invalid `:GU#` prevents every mutating command.

### Field test progression

1. Deploy with `StartupPolicy=probe`, `BackgroundPolicy=validate`; record
   residuals only.
2. Run several Calibrate-mode, operator-started controlled three-point sessions
   and verify at a fourth, independent location.
3. Enable conservative one-point sync with a long cooldown and inspect logs.
4. Adjust thresholds only from recorded field behavior.
5. Expand or alter the fixed target plan only after the rig's travel envelope
   and recovery behavior are proven.

## Acceptance criteria

- Startup never moves or synchronizes the mount under default settings.
- An unsafe/unknown OnStep state results in no mutation command.
- Every accepted calibration is explainable from dashboard/log evidence:
  status, two solve results, residual, gate decisions, and protocol replies.
- No more than one calibration transaction runs at a time.
- Assisted alignment commands only its configured, in-envelope targets, adds
  exactly three accepted points, and is independently verified at a fourth
  pointing.
- A failed network operation or restart leaves StepSolve operational and does
  not cause an unrequested new alignment session.

## Decisions requested before implementation

1. Should a connected OnStep default to `validate` (recommended) or `off`?
2. Is a one-point automatic correction desirable at all, or should all
   corrections be user-accepted initially?
3. What physical home/park procedure should be required before the three-point
   workflow on this Alt-Az rig, and how close to 0,0 is acceptable before
   StepSolve begins the first controlled GoTo? Proposed answer: true north and
   level within approximately ±5°.
4. Are the proposed initial limits (2° maximum automatic correction, 15-minute
   cooldown, 15° separation) suitably conservative for the first field phase?
5. Which three initial Alt/Az targets and ±10° fallback direction best respect
   the actual cable path, tree line, and physical stops at the test site?
