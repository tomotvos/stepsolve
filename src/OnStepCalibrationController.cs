using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace StepSolve;

/// <summary>
/// Owns the deliberately operator-paced OnStep three-point alignment session.
/// It only accepts solves supplied by the Calibrate capture loop, and it never
/// advances a plate-solved point without an explicit API approval.
/// </summary>
public sealed class OnStepCalibrationController : IOnStepCalibrationSession
{
    private readonly OnStepClient _onstep;
    private readonly IOptionsMonitor<OnStepOptions> _options;
    private readonly ILogger<OnStepCalibrationController> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private OnStepCalibrationStatus _status = IdleStatus();
    private List<OnStepCalibrationTarget> _targets = [];
    private int _pointIndex;
    private int _attempt;
    private DateTimeOffset _settlingSince;
    private SolveResult? _firstStableSolve;
    private DateTimeOffset _firstStableSolveAt;
    private SolveResult? _candidate;

    public OnStepCalibrationController(
        OnStepClient onstep,
        IOptionsMonitor<OnStepOptions> options,
        ILogger<OnStepCalibrationController> logger)
    {
        _onstep = onstep;
        _options = options;
        _logger = logger;
    }

    public OnStepCalibrationStatus Status => Volatile.Read(ref _status);

    public bool NeedsFreshSolve => Status.State == "AwaitingStableSolves";

    public async Task InitializeAsync(CancellationToken ct)
    {
        if (IsActive(Status.State))
            return;

        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            SetStatus("Idle", false, false, "OnStep is disabled in Settings.");
            return;
        }

        if (!string.Equals(options.StartupPolicy, "probe", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Idle", false, false,
                $"Startup policy '{options.StartupPolicy}' is not enabled; select probe for read-only connection validation.");
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            try
            {
                var identity = await _onstep.ProbeAsync(ct);
                var mount = await _onstep.GetStatusAsync(ct);
                SetStatus("Idle", true, IsSafe(mount),
                    IsSafe(mount)
                        ? $"Connected to {identity.Product} {identity.FirmwareVersion}; mount is safe for alignment."
                        : $"Connected to {identity.Product} {identity.FirmwareVersion}; mount is not safe for alignment.",
                    mount.Raw);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OnStep startup probe failed");
                SetStatus("Idle", false, false, $"OnStep probe failed: {ex.Message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CalibrationActionResult> StartAsync(bool confirmed, string currentMode, CancellationToken ct)
    {
        if (!confirmed)
            return new(false, "Starting alignment requires explicit confirmation.");
        if (!IsCalibrateMode(currentMode))
            return new(false, "OnStep alignment can only start in Calibrate mode.");

        await _gate.WaitAsync(ct);
        try
        {
            if (IsActive(Status.State))
                return new(false, "An OnStep calibration session is already active.");

            var options = _options.CurrentValue;
            if (!options.Enabled)
            {
                SetStatus("Idle", false, false, "OnStep is disabled in Settings.");
                return new(false, "OnStep is disabled in Settings.");
            }

            var validation = ValidateTargetPlan(options);
            if (validation != null)
            {
                SetStatus("Failed", false, false, validation);
                return new(false, validation);
            }

            try
            {
                var identity = await _onstep.ProbeAsync(ct);
                var mount = await _onstep.GetStatusAsync(ct);
                if (!IsSafe(mount))
                {
                    SetStatus("Failed", true, false,
                        "Mount must be idle, unparked, and not homing before alignment.", mount.Raw);
                    return new(false, "Mount is not in a safe state for alignment.");
                }

                var started = await _onstep.StartAlignmentAsync(3, ct);
                if (!started.Succeeded)
                {
                    SetStatus("Failed", true, true, started.Error, started.Response);
                    return new(false, started.Error ?? "OnStep rejected the three-point alignment command.");
                }

                _targets = options.CalibrationTargets.ToList();
                _pointIndex = 0;
                _attempt = 1;
                _firstStableSolve = null;
                _candidate = null;
                SetStatus("StartingAlignment", true, true,
                    $"Connected to {identity.Product} {identity.FirmwareVersion}; starting point 1.", started.Response);

                return await CommandCurrentTargetAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OnStep alignment preflight failed");
                SetStatus("Failed", false, false, $"OnStep preflight failed: {ex.Message}");
                return new(false, $"OnStep preflight failed: {ex.Message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        if (Status.State is not ("WaitingForGoto" or "Settling"))
            return;

        await _gate.WaitAsync(ct);
        try
        {
            if (Status.State == "WaitingForGoto")
            {
                try
                {
                    var mount = await _onstep.GetStatusAsync(ct);
                    if (!IsSafeOrSlewing(mount))
                    {
                        SetStatus("Failed", true, false,
                            "Mount entered an unsafe state while moving.", mount.Raw);
                        return;
                    }

                    if (mount.IsSlewing)
                    {
                        SetStatus("WaitingForGoto", true, true, "Waiting for OnStep GoTo to finish.", mount.Raw);
                        return;
                    }

                    _settlingSince = DateTimeOffset.UtcNow;
                    SetStatus("Settling", true, true, "GoTo complete; waiting for the mount to settle.", mount.Raw);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unable to poll OnStep GoTo state");
                    SetStatus("Failed", false, false, $"Unable to poll OnStep: {ex.Message}");
                }
                return;
            }

            var settle = TimeSpan.FromSeconds(_options.CurrentValue.CalibrationSettleSeconds);
            if (DateTimeOffset.UtcNow - _settlingSince < settle)
                return;

            _firstStableSolve = null;
            _candidate = null;
            SetStatus("AwaitingStableSolves", true, true,
                "Mount settled. Collecting two fresh, agreeing plate solves.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SubmitFreshSolveAsync(SolveResult result, CancellationToken ct)
    {
        if (Status.State != "AwaitingStableSolves")
            return;

        await _gate.WaitAsync(ct);
        try
        {
            if (Status.State != "AwaitingStableSolves")
                return;

            var options = _options.CurrentValue;
            if (!result.IsValid || result.Confidence < options.MinSolveConfidence)
            {
                _logger.LogInformation("OnStep calibration point {Point} did not produce a usable solve", _pointIndex + 1);
                await TryAlternateTargetAsync("No plate solve met the confidence threshold; trying the alternate target.", ct);
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (_firstStableSolve == null)
            {
                _firstStableSolve = result;
                _firstStableSolveAt = now;
                SetStatus("AwaitingStableSolves", true, true,
                    "First fresh solve received; waiting for a second agreeing solve.");
                return;
            }

            if (now - _firstStableSolveAt < TimeSpan.FromSeconds(options.StableSolveIntervalSeconds))
                return;

            var firstSolve = _firstStableSolve.Value;
            var disagreement = OnStepClient.AngularDistance(
                firstSolve.RaDeg, firstSolve.DecDeg, result.RaDeg, result.DecDeg);
            if (disagreement > options.MaxSolveDisagreementDeg)
            {
                _firstStableSolve = result;
                _firstStableSolveAt = now;
                SetStatus("AwaitingStableSolves", true, true,
                    $"Solve disagreement was {disagreement:F3}°; using the latest solve as a new first sample.");
                return;
            }

            _candidate = result;
            SetStatus("AwaitingAcceptance", true, true,
                $"Stable solve candidate ready (agreement {disagreement:F3}°). Approve it to add point {_pointIndex + 1}.",
                candidate: result);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CalibrationActionResult> AcceptAsync(string currentMode, CancellationToken ct)
    {
        if (!IsCalibrateMode(currentMode))
            return new(false, "OnStep alignment can only be accepted in Calibrate mode.");

        await _gate.WaitAsync(ct);
        try
        {
            if (Status.State != "AwaitingAcceptance" || _candidate == null)
                return new(false, "There is no stable calibration point awaiting approval.");

            var candidate = _candidate.Value;
            SetStatus("AcceptingPoint", true, true, $"Submitting approved point {_pointIndex + 1} to OnStep.", candidate: candidate);
            try
            {
                var accepted = await _onstep.SyncSolvedPositionAsync(candidate, ct);
                if (!accepted.Succeeded)
                {
                    SetStatus("Failed", true, true, accepted.Error, accepted.Response, candidate);
                    return new(false, accepted.Error ?? "OnStep rejected the solved calibration point.");
                }

                var progress = await _onstep.GetAlignmentProgressAsync(ct);
                if (_pointIndex + 1 < _targets.Count && !progress.IsActive)
                {
                    SetStatus("Failed", true, false,
                        "OnStep unexpectedly ended the alignment sequence before all points were accepted.", accepted.Response);
                    return new(false, "OnStep ended the alignment sequence unexpectedly.");
                }

                _logger.LogInformation("OnStep calibration point {Point} accepted: RA={Ra:F4} Dec={Dec:F4}",
                    _pointIndex + 1, candidate.RaDeg, candidate.DecDeg);
                _pointIndex++;
                _attempt = 1;
                _firstStableSolve = null;
                _candidate = null;

                if (_pointIndex == _targets.Count)
                {
                    SetStatus("Completed", true, true,
                        "All three plate-solved points were accepted by OnStep.", accepted.Response);
                    return new(true, null);
                }

                SetStatus("GotoPoint", true, true,
                    $"Point {_pointIndex} accepted (OnStep alignment {progress.CurrentStar}/{progress.MaximumStars}); moving to point {_pointIndex + 1}.", accepted.Response);
                return await CommandCurrentTargetAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OnStep failed while accepting calibration point");
                SetStatus("Failed", false, false, $"OnStep sync failed: {ex.Message}");
                return new(false, $"OnStep sync failed: {ex.Message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CalibrationActionResult> AbortAsync(string currentMode, CancellationToken ct)
    {
        if (!IsCalibrateMode(currentMode))
            return new(false, "OnStep alignment can only be aborted in Calibrate mode.");

        await _gate.WaitAsync(ct);
        try
        {
            if (!IsActive(Status.State))
                return new(true, null);

            _firstStableSolve = null;
            _candidate = null;
            SetStatus("Aborted", Status.IsConnected, Status.IsSafe,
                "Alignment aborted locally. OnStep may retain an incomplete alignment session.");
            _logger.LogWarning("OnStep calibration session aborted by operator");
            return new(true, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CalibrationActionResult> CommandCurrentTargetAsync(CancellationToken ct)
    {
        if (_pointIndex >= _targets.Count)
            return new(false, "No calibration target is available.");

        var options = _options.CurrentValue;
        if (_attempt > options.CalibrationTargetRetryCount)
        {
            SetStatus("Failed", true, true,
                $"Point {_pointIndex + 1} did not solve after {options.CalibrationTargetRetryCount} target attempts.");
            return new(false, "The calibration point did not solve within the configured retry limit.");
        }

        var target = TargetForAttempt(_targets[_pointIndex], _attempt);
        var targetError = ValidateTarget(target, options);
        if (targetError != null)
        {
            SetStatus("Failed", true, false, targetError);
            return new(false, targetError);
        }

        SetStatus("GotoPoint", true, true,
            $"Moving to point {_pointIndex + 1}, attempt {_attempt}: Az {target.AzimuthDeg:F1}°, Alt {target.AltitudeDeg:F1}°.");
        var goTo = await _onstep.GotoAltAzAsync(target.AltitudeDeg, target.AzimuthDeg, ct);
        if (!goTo.Succeeded)
        {
            SetStatus("Failed", true, true, goTo.Error, goTo.Response);
            return new(false, goTo.Error ?? "OnStep rejected the calibration GoTo.");
        }

        SetStatus("WaitingForGoto", true, true,
            $"GoTo accepted for point {_pointIndex + 1}, attempt {_attempt}.", goTo.Response);
        return new(true, null);
    }

    private async Task TryAlternateTargetAsync(string reason, CancellationToken ct)
    {
        _attempt++;
        _firstStableSolve = null;
        _candidate = null;
        if (_attempt > _options.CurrentValue.CalibrationTargetRetryCount)
        {
            SetStatus("Failed", true, true,
                $"{reason} Retry limit reached for point {_pointIndex + 1}.");
            return;
        }

        SetStatus("GotoPoint", true, true, reason);
        await CommandCurrentTargetAsync(ct);
    }

    private void SetStatus(
        string state,
        bool connected,
        bool safe,
        string? message,
        string? reply = null,
        SolveResult? candidate = null)
    {
        var target = _targets.Count > _pointIndex ? TargetForAttempt(_targets[_pointIndex], Math.Max(1, _attempt)) : null;
        Volatile.Write(ref _status, new OnStepCalibrationStatus(
            state,
            connected,
            safe,
            message,
            _targets.Count > 0 && _pointIndex < _targets.Count ? _pointIndex + 1 : 0,
            _targets.Count > 0 && _pointIndex < _targets.Count ? _attempt : 0,
            target?.AzimuthDeg,
            target?.AltitudeDeg,
            candidate?.RaDeg,
            candidate?.DecDeg,
            reply));
    }

    private static OnStepCalibrationStatus IdleStatus() =>
        new("Idle", false, false, "OnStep alignment has not started.", 0, 0, null, null, null, null, null);

    private static bool IsCalibrateMode(string mode) =>
        string.Equals(mode, "calibrate", StringComparison.OrdinalIgnoreCase);

    private static bool IsActive(string state) => state is not ("Idle" or "Completed" or "Aborted" or "Failed");

    private static bool IsSafe(OnStepMountStatus mount) =>
        !mount.IsSlewing && !mount.IsParked && !mount.IsParking && !mount.IsHoming;

    private static bool IsSafeOrSlewing(OnStepMountStatus mount) =>
        !mount.IsParked && !mount.IsParking && !mount.IsHoming;

    private static OnStepCalibrationTarget TargetForAttempt(OnStepCalibrationTarget baseTarget, int attempt) => attempt switch
    {
        1 => baseTarget,
        2 => baseTarget with { AzimuthDeg = baseTarget.AzimuthDeg + 10 },
        3 => baseTarget with { AzimuthDeg = baseTarget.AzimuthDeg - 10 },
        _ => baseTarget,
    };

    private static string? ValidateTargetPlan(OnStepOptions options)
    {
        if (options.CalibrationTargets.Count != 3)
            return "OnStep calibration requires exactly three configured targets.";
        if (options.CalibrationTargetRetryCount is < 1 or > 3)
            return "CalibrationTargetRetryCount must be between 1 and 3.";
        if (options.CalibrationSettleSeconds < 0 || options.StableSolveIntervalSeconds < 0)
            return "Calibration timing values cannot be negative.";
        if (options.MinSolveConfidence is < 0 or > 1 || options.MaxSolveDisagreementDeg <= 0)
            return "Calibration solve thresholds are invalid.";
        return options.CalibrationTargets.Select(target => ValidateTarget(target, options)).FirstOrDefault(error => error != null);
    }

    private static string? ValidateTarget(OnStepCalibrationTarget target, OnStepOptions options)
    {
        if (target.AltitudeDeg < options.CalibrationMinAltitudeDeg || target.AltitudeDeg > options.CalibrationMaxAltitudeDeg)
            return $"Target altitude {target.AltitudeDeg:F1}° is outside the configured safe envelope.";
        if (target.AzimuthDeg < options.CalibrationMinAzimuthDeg || target.AzimuthDeg > options.CalibrationMaxAzimuthDeg)
            return $"Target azimuth {target.AzimuthDeg:F1}° is outside the configured safe envelope.";
        return null;
    }
}
