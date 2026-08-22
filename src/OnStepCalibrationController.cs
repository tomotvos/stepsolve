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
    private readonly SettingsService? _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private OnStepCalibrationStatus _status = IdleStatus();
    private List<OnStepCalibrationTarget> _targets = [];
    private int _pointIndex;
    private int _attempt;
    private DateTimeOffset _settlingSince;
    private DateTimeOffset _nextMotionPollAt = DateTimeOffset.MinValue;
    private SolveResult? _firstStableSolve;
    private DateTimeOffset _nextSolveAttemptAt = DateTimeOffset.MinValue;
    private SolveResult? _candidate;
    private SolveResult? _firstAutomaticCorrectionSolve;
    private DateTimeOffset _firstAutomaticCorrectionSolveAt;
    private DateTimeOffset _lastAutomaticCorrectionAt;
    private bool _simulationEnabled;
    private int _simulationSample;
    private bool _probePending = true;
    private DateTimeOffset _nextProbeAttemptAt = DateTimeOffset.MinValue;
    private bool? _lastConfiguredEnabled;
    private string? _lastConfiguredHost;
    private int _lastConfiguredPort;
    private string? _lastConfiguredStartupPolicy;
    private static readonly TimeSpan ProbeRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HealthyProbeInterval = TimeSpan.FromSeconds(15);
    private const double MinimumAutomaticSolveConfidence = 0.90;
    private const double MaximumAutomaticSolveDisagreementDeg = 0.05;
    private const int MinimumAutomaticStabilitySeconds = 1;
    private const int MaximumAutomaticStabilitySeconds = 300;
    private const int MinimumAutomaticCorrectionIntervalMinutes = 15;
    private const int MaximumAutomaticCorrectionIntervalMinutes = 1440;
    private const double MaximumAutomaticCorrectionDeg = 1.0;

    public OnStepCalibrationController(
        OnStepClient onstep,
        IOptionsMonitor<OnStepOptions> options,
        ILogger<OnStepCalibrationController> logger,
        SettingsService? settings = null)
    {
        _onstep = onstep;
        _options = options;
        _logger = logger;
        _settings = settings;
        _lastAutomaticCorrectionAt = options.CurrentValue.LastAutomaticCorrectionAtUtc ?? default;
    }

    public OnStepCalibrationStatus Status => Volatile.Read(ref _status);

    // After a disagreement, this temporarily becomes false to prevent the
    // capture loop from spending solver time until the retry backoff expires.
    public bool NeedsFreshSolve => (Status.State is "AwaitingStableSolves" or "RecoveringHomeSolves") &&
        DateTimeOffset.UtcNow >= _nextSolveAttemptAt;
    public bool WantsImmediateFollowUpSolve => _firstStableSolve != null && NeedsFreshSolve;
    public bool UsesSimulatedSolves => _simulationEnabled && NeedsFreshSolve;

    public async Task<CalibrationActionResult> SetSimulationAsync(bool enabled, string currentMode, CancellationToken ct)
    {
        if (!IsCalibrateMode(currentMode))
            return new(false, "Simulation can only be changed in Calibrate mode.");

        await _gate.WaitAsync(ct);
        try
        {
            if (IsActive(Status.State))
                return new(false, "Simulation cannot be changed while an alignment session is active.");

            _simulationEnabled = enabled;
            _simulationSample = 0;
            SetStatus(Status.State, Status.IsConnected, Status.IsSafe,
                enabled
                    ? "Simulation enabled for the next alignment session. Simulated solves use OnStep's reported position."
                    : "Simulation disabled. Calibration will use fresh camera plate solves.",
                Status.LastReply);
            return new(true, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SolveResult> CreateSimulatedSolveAsync(CancellationToken ct)
    {
        if (!UsesSimulatedSolves)
            throw new InvalidOperationException("Simulated solves are not enabled for the active calibration state.");

        var reported = await _onstep.GetPositionAsync(ct);
        // Deterministic, very small opposing offsets let the existing stability
        // gate execute exactly as it would with two real fresh plate solves.
        var offset = Interlocked.Increment(ref _simulationSample) % 2 == 0 ? -0.004 : 0.004;
        var dec = Math.Clamp(reported.DecDeg - offset, -89.999, 89.999);
        return new SolveResult(
            reported.RaDeg + offset,
            dec,
            RollDeg: null,
            PlateScaleArcsecPerPx: null,
            Confidence: 0.99,
            SolveTime: TimeSpan.Zero,
            SolverName: "onstep-simulation");
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        if (IsActive(Status.State))
            return;

        await _gate.WaitAsync(ct);
        try
        {
            if (IsActive(Status.State))
                return;

            var options = _options.CurrentValue;
            var configurationChanged = options.Enabled != _lastConfiguredEnabled ||
                !string.Equals(options.Host, _lastConfiguredHost, StringComparison.OrdinalIgnoreCase) ||
                options.Port != _lastConfiguredPort ||
                !string.Equals(options.StartupPolicy, _lastConfiguredStartupPolicy, StringComparison.OrdinalIgnoreCase);
            _lastConfiguredEnabled = options.Enabled;
            _lastConfiguredHost = options.Host;
            _lastConfiguredPort = options.Port;
            _lastConfiguredStartupPolicy = options.StartupPolicy;

            if (!options.Enabled)
            {
                _probePending = false;
                if (configurationChanged || Status.IsConnected)
                    SetStatus("Idle", false, false, "OnStep is disabled in Settings.");
                return;
            }

            if (!string.Equals(options.StartupPolicy, "probe", StringComparison.OrdinalIgnoreCase))
            {
                _probePending = false;
                if (configurationChanged || Status.IsConnected)
                    SetStatus("Idle", false, false,
                        $"Startup policy '{options.StartupPolicy}' is not enabled; select probe for read-only connection validation.");
                return;
            }

            if (configurationChanged)
            {
                _probePending = true;
                _nextProbeAttemptAt = DateTimeOffset.MinValue;
            }

            // A failed action that lost its transport must recover without a
            // service restart. Conversely, a successful idle probe is checked
            // periodically so the UI cannot retain a stale "Connected / safe"
            // state after the controller is powered down.
            if (Status.State == "Failed" && !Status.IsConnected)
                _probePending = true;

            var now = DateTimeOffset.UtcNow;
            var healthyCheckDue = Status.State == "Idle" && Status.IsConnected && now >= _nextProbeAttemptAt;
            if ((!_probePending && !healthyCheckDue) || now < _nextProbeAttemptAt)
                return;

            try
            {
                var recovering = _probePending || !Status.IsConnected;
                if (recovering)
                    _logger.LogInformation("OnStep probe starting ({Host}:{Port})", options.Host, options.Port);
                var identity = await _onstep.ProbeAsync(ct);
                var mount = await _onstep.GetStatusAsync(ct);
                _probePending = false;
                _nextProbeAttemptAt = now.Add(HealthyProbeInterval);
                if (recovering)
                    _logger.LogInformation("OnStep probe succeeded: {Product} {FirmwareVersion}", identity.Product, identity.FirmwareVersion);
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
                _nextProbeAttemptAt = DateTimeOffset.UtcNow.Add(ProbeRetryDelay);
                _logger.LogWarning(ex, "OnStep probe failed; retrying in {RetryDelaySeconds}s", ProbeRetryDelay.TotalSeconds);
                SetStatus("Idle", false, false, $"OnStep probe failed: {ex.Message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CalibrationActionResult> StartAsync(
        bool confirmed,
        CalibrationHomeStrategy homeStrategy,
        string currentMode,
        CancellationToken ct)
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

                // The guided V1 target plan includes Alt 80°. Deliberately set
                // the firmware overhead limit once, before starting alignment,
                // so it cannot reject that safe planned target.
                var overhead = await _onstep.SetOverheadLimitAsync(90, ct);
                if (!overhead.Succeeded)
                {
                    SetStatus("Failed", true, true, overhead.Error, overhead.Response);
                    return new(false, overhead.Error ?? "OnStep rejected the overhead-limit update.");
                }

                _targets = options.CalibrationTargets.ToList();
                _pointIndex = 0;
                _attempt = 1;
                _firstStableSolve = null;
                _candidate = null;
                _nextSolveAttemptAt = DateTimeOffset.MinValue;
                _simulationSample = 0;
                return homeStrategy switch
                {
                    CalibrationHomeStrategy.AtHome => await StartThreePointAlignmentAtHomeAsync(
                        $"Connected to {identity.Product} {identity.FirmwareVersion}; operator confirmed the rig is physically at Home.", ct),
                    CalibrationHomeStrategy.ReturnToHome => await ReturnToHomeBeforeAlignmentAsync(
                        $"Connected to {identity.Product} {identity.FirmwareVersion}; returning mount to Home before point 1.", ct),
                    CalibrationHomeStrategy.RecoverHome => BeginHomeRecovery(
                        $"Connected to {identity.Product} {identity.FirmwareVersion}; collect two matching plate solves to recover current pointing before returning Home."),
                    _ => new(false, "Unknown Home strategy."),
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OnStep alignment preflight failed");
                RequestConnectionRecovery();
                SetStatus("Failed", false, false, $"OnStep preflight failed: {ex.Message}");
                return new(false, $"OnStep preflight failed: {ex.Message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // Retained for existing in-process callers. New API callers must choose a
    // Home strategy explicitly; the historical behavior was to return Home.
    public Task<CalibrationActionResult> StartAsync(bool confirmed, string currentMode, CancellationToken ct) =>
        StartAsync(confirmed, CalibrationHomeStrategy.ReturnToHome, currentMode, ct);

    public async Task TickAsync(CancellationToken ct)
    {
        if (Status.State is not ("ReturningHome" or "WaitingForGoto" or "Settling"))
            return;

        await _gate.WaitAsync(ct);
        try
        {
            if ((Status.State is "ReturningHome" or "WaitingForGoto") &&
                DateTimeOffset.UtcNow < _nextMotionPollAt)
                return;

            if (Status.State == "ReturningHome")
            {
                try
                {
                    var mount = await _onstep.GetStatusAsync(ct);
                    if (mount.IsParked || mount.IsParking)
                    {
                        SetStatus("Failed", true, false,
                            "Mount entered a parked state while returning Home.", mount.Raw);
                        return;
                    }

                    // :hC# has no reply.  Do not infer completion from N / not
                    // slewing: OnStep documents H as the authoritative Home flag.
                    if (!mount.IsAtHome)
                    {
                        ScheduleNextMotionPoll();
                        SetStatus("ReturningHome", true, true,
                            "Returning mount to Home; waiting for OnStep Home (H) status.", mount.Raw);
                        return;
                    }

                    await StartThreePointAlignmentAtHomeAsync("Mount is at Home; starting point 1.", ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unable to poll OnStep Home state");
                    RequestConnectionRecovery();
                    SetStatus("Failed", false, false, $"Unable to confirm Home: {ex.Message}");
                }
                return;
            }

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
                        ScheduleNextMotionPoll();
                        SetStatus("WaitingForGoto", true, true, "Waiting for OnStep GoTo to finish.", mount.Raw);
                        return;
                    }

                    _nextMotionPollAt = DateTimeOffset.MinValue;
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
                    RequestConnectionRecovery();
                    SetStatus("Failed", false, false, $"Unable to poll OnStep: {ex.Message}");
                }
                return;
            }

            var settle = TimeSpan.FromSeconds(_options.CurrentValue.CalibrationSettleSeconds);
            if (DateTimeOffset.UtcNow - _settlingSince < settle)
                return;

            _firstStableSolve = null;
            _candidate = null;
            _nextSolveAttemptAt = DateTimeOffset.MinValue;
            SetStatus("AwaitingStableSolves", true, true,
                "Mount settled. Collecting two plate solves to verify pointing.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SubmitFreshSolveAsync(SolveResult result, CancellationToken ct)
    {
        if (Status.State is not ("AwaitingStableSolves" or "RecoveringHomeSolves"))
            return;

        await _gate.WaitAsync(ct);
        try
        {
            if (Status.State is not ("AwaitingStableSolves" or "RecoveringHomeSolves"))
                return;

            var options = _options.CurrentValue;
            if (Status.State == "RecoveringHomeSolves")
            {
                await SubmitHomeRecoverySolveAsync(result, options, ct);
                return;
            }

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
                SetStatus("AwaitingStableSolves", true, true,
                    "First solve received; waiting for a second solve to confirm it.");
                return;
            }

            var firstSolve = _firstStableSolve.Value;
            var disagreement = OnStepClient.AngularDistance(
                firstSolve.RaDeg, firstSolve.DecDeg, result.RaDeg, result.DecDeg);
            if (disagreement > options.MaxSolveDisagreementDeg)
            {
                _firstStableSolve = result;
                _nextSolveAttemptAt = now.AddSeconds(options.StableSolveIntervalSeconds);
                SetStatus("AwaitingStableSolves", true, true,
                    $"Solves differed by {disagreement:F3}°; retrying comparison after {options.StableSolveIntervalSeconds}s.");
                return;
            }

            _candidate = result;
            SetStatus("AwaitingAcceptance", true, true,
                $"Pointing verified (solves differ by {disagreement:F3}°). Approve to add point {_pointIndex + 1}.",
                candidate: result);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Considers a fresh Solve-mode result for a one-point automatic correction.
    /// This is deliberately separate from the guided three-point alignment flow:
    /// it never slews or starts an alignment and it is inert until enabled by the
    /// operator. A status check immediately before the mutation prevents a sync
    /// while OnStep is reporting a GoTo or other motion beyond normal tracking.
    /// </summary>
    public async Task SubmitAutomaticCorrectionCandidateAsync(SolveResult result, CancellationToken ct)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled || !options.AutomaticCorrectionsEnabled || IsActive(Status.State))
        {
            _firstAutomaticCorrectionSolve = null;
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            // Re-read configuration after waiting for an active protocol action.
            options = _options.CurrentValue;
            if (!options.Enabled || !options.AutomaticCorrectionsEnabled || IsActive(Status.State))
            {
                _firstAutomaticCorrectionSolve = null;
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (!double.IsFinite(options.MinSolveConfidence))
            {
                _logger.LogWarning("Automatic OnStep correction disabled by invalid safety configuration.");
                _firstAutomaticCorrectionSolve = null;
                return;
            }
            var minimumConfidence = Math.Clamp(options.MinSolveConfidence, MinimumAutomaticSolveConfidence, 1.0);
            var stabilitySeconds = Math.Clamp(options.StableSolveIntervalSeconds,
                MinimumAutomaticStabilitySeconds, MaximumAutomaticStabilitySeconds);
            var correctionIntervalMinutes = Math.Clamp(options.CorrectionIntervalMinutes,
                MinimumAutomaticCorrectionIntervalMinutes, MaximumAutomaticCorrectionIntervalMinutes);
            if (!double.IsFinite(options.MaxSolveDisagreementDeg) || options.MaxSolveDisagreementDeg <= 0 ||
                !double.IsFinite(options.MaxAutomaticCorrectionDeg) || options.MaxAutomaticCorrectionDeg <= 0)
            {
                _logger.LogWarning("Automatic OnStep correction disabled by invalid safety configuration.");
                _firstAutomaticCorrectionSolve = null;
                return;
            }
            var maximumDisagreement = Math.Min(options.MaxSolveDisagreementDeg, MaximumAutomaticSolveDisagreementDeg);
            var maximumCorrection = Math.Min(options.MaxAutomaticCorrectionDeg, MaximumAutomaticCorrectionDeg);
            if (options.LastAutomaticCorrectionAtUtc > _lastAutomaticCorrectionAt)
                _lastAutomaticCorrectionAt = options.LastAutomaticCorrectionAtUtc.Value;
            if (_lastAutomaticCorrectionAt != default &&
                now - _lastAutomaticCorrectionAt < TimeSpan.FromMinutes(correctionIntervalMinutes))
                return;

            if (!result.IsValid || !double.IsFinite(result.RaDeg) || !double.IsFinite(result.DecDeg) ||
                !double.IsFinite(result.Confidence) || result.Confidence < minimumConfidence)
            {
                // The two qualifying solves must be consecutive fresh captures.
                _firstAutomaticCorrectionSolve = null;
                return;
            }

            if (_firstAutomaticCorrectionSolve == null)
            {
                _firstAutomaticCorrectionSolve = result;
                _firstAutomaticCorrectionSolveAt = now;
                return;
            }

            var maximumCandidateAge = TimeSpan.FromSeconds(Math.Max(10, stabilitySeconds * 3));
            if (now - _firstAutomaticCorrectionSolveAt > maximumCandidateAge)
            {
                _firstAutomaticCorrectionSolve = result;
                _firstAutomaticCorrectionSolveAt = now;
                return;
            }

            var first = _firstAutomaticCorrectionSolve.Value;
            var disagreement = OnStepClient.AngularDistance(first.RaDeg, first.DecDeg, result.RaDeg, result.DecDeg);
            if (disagreement > maximumDisagreement)
            {
                _logger.LogInformation("Automatic OnStep correction deferred: consecutive solves differ by {Disagreement:F3}°", disagreement);
                _firstAutomaticCorrectionSolve = result;
                _firstAutomaticCorrectionSolveAt = now;
                return;
            }
            if (now - _firstAutomaticCorrectionSolveAt < TimeSpan.FromSeconds(stabilitySeconds))
                return;

            var mount = await _onstep.GetStatusAsync(ct);
            if (!IsSafe(mount))
            {
                _logger.LogInformation("Automatic OnStep correction deferred: mount is moving, parked, parking, or homing ({Status})", mount.Raw);
                _firstAutomaticCorrectionSolve = null;
                return;
            }

            var reported = await _onstep.GetPositionAsync(ct);
            if (!double.IsFinite(reported.RaDeg) || !double.IsFinite(reported.DecDeg))
            {
                _logger.LogWarning("Automatic OnStep correction rejected: OnStep returned non-finite coordinates.");
                _firstAutomaticCorrectionSolve = null;
                return;
            }
            var residual = OnStepClient.AngularDistance(reported.RaDeg, reported.DecDeg, result.RaDeg, result.DecDeg);
            if (!double.IsFinite(residual) || residual > maximumCorrection)
            {
                _logger.LogWarning(
                    "Automatic OnStep correction rejected: measured delta {Residual:F3}° exceeds maximum automatic correction {Maximum:F3}°",
                    residual, maximumCorrection);
                _firstAutomaticCorrectionSolve = null;
                return;
            }

            // Status may have changed while reading the mount position; check
            // immediately before the non-idempotent :Sr/:Sd/:CM# exchange.
            mount = await _onstep.GetStatusAsync(ct);
            if (!IsSafe(mount))
            {
                _logger.LogInformation("Automatic OnStep correction deferred: mount began moving ({Status})", mount.Raw);
                _firstAutomaticCorrectionSolve = null;
                return;
            }

            // Settings are hot-reloaded. Revoking automatic correction must
            // take effect before the non-idempotent mount command is sent.
            options = _options.CurrentValue;
            if (!options.Enabled || !options.AutomaticCorrectionsEnabled)
            {
                _logger.LogInformation("Automatic OnStep correction cancelled because it was disabled in Settings.");
                _firstAutomaticCorrectionSolve = null;
                return;
            }
            if (_lastAutomaticCorrectionAt != default &&
                now - _lastAutomaticCorrectionAt < TimeSpan.FromMinutes(Math.Clamp(options.CorrectionIntervalMinutes,
                    MinimumAutomaticCorrectionIntervalMinutes, MaximumAutomaticCorrectionIntervalMinutes)))
            {
                _firstAutomaticCorrectionSolve = null;
                return;
            }
            if (!double.IsFinite(options.MaxAutomaticCorrectionDeg) || options.MaxAutomaticCorrectionDeg <= 0 ||
                residual > Math.Min(options.MaxAutomaticCorrectionDeg, MaximumAutomaticCorrectionDeg))
            {
                _logger.LogWarning(
                    "Automatic OnStep correction rejected: measured delta {Residual:F3}° exceeds maximum automatic correction {Maximum:F3}°",
                    residual, Math.Min(options.MaxAutomaticCorrectionDeg, MaximumAutomaticCorrectionDeg));
                _firstAutomaticCorrectionSolve = null;
                return;
            }

            await _onstep.SyncAsync(result, ct);
            if (_onstep.LastSyncResult == "ok")
            {
                _lastAutomaticCorrectionAt = DateTimeOffset.UtcNow;
                _settings?.RecordAutomaticCorrection(_lastAutomaticCorrectionAt);
                _logger.LogInformation("Automatic OnStep correction applied: residual {Residual:F3}°", residual);
            }
            _firstAutomaticCorrectionSolve = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // SyncAsync normally absorbs transport errors, but the read-only
            // safety queries can fail. Keep the solve loop healthy and allow a
            // later fresh pair to try again.
            _logger.LogWarning(ex, "Automatic OnStep correction safety check failed");
            _firstAutomaticCorrectionSolve = null;
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
                var accepted = await _onstep.AcceptAlignmentPointAsync(candidate, ct);
                if (!accepted.Succeeded)
                {
                    SetStatus("Failed", true, true, accepted.Error, accepted.Response, candidate);
                    return new(false, accepted.Error ?? "OnStep rejected the solved calibration point.");
                }

                // This is deliberately logged before the read-only :A?#
                // verification. If that follow-up query loses its transport,
                // operators can still distinguish an accepted point from one
                // that never reached OnStep.
                _logger.LogInformation(
                    "OnStep accepted calibration point {Point}: RA={Ra:F4} Dec={Dec:F4}; verifying alignment progress",
                    _pointIndex + 1, candidate.RaDeg, candidate.DecDeg);
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
                _nextSolveAttemptAt = DateTimeOffset.MinValue;
                _candidate = null;

                if (_pointIndex == _targets.Count)
                {
                    // The alignment is over and no background OnStep operation is
                    // active yet. Release the session socket so the dashboard does
                    // not imply that a controller that is later powered down is
                    // still connected.
                    await _onstep.CloseConnectionAsync(CancellationToken.None);
                    SetStatus("Completed", false, false,
                        "All three plate-solved points were accepted by OnStep; connection closed after session completion.",
                        accepted.Response);
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
                RequestConnectionRecovery();
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

    public async Task<CalibrationActionResult> ReconnectAsync(string currentMode, CancellationToken ct)
    {
        if (!IsCalibrateMode(currentMode))
            return new(false, "OnStep reconnect can only be requested in Calibrate mode.");

        await _gate.WaitAsync(ct);
        try
        {
            if (IsActive(Status.State))
                return new(false, "Cannot reconnect while an alignment session is active.");
            if (!_options.CurrentValue.Enabled)
                return new(false, "OnStep is disabled in Settings.");

            await _onstep.CloseConnectionAsync(ct);
            RequestConnectionRecovery();
            SetStatus("Idle", false, false, "Reconnect requested; probing OnStep now.");
            _logger.LogInformation("OnStep reconnect requested by operator");
            return new(true, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private CalibrationActionResult BeginHomeRecovery(string message)
    {
        SetStatus("RecoveringHomeSolves", true, true, message);
        _logger.LogInformation("OnStep Home recovery started; waiting for two stable plate solves");
        return new(true, null);
    }

    private async Task<CalibrationActionResult> ReturnToHomeBeforeAlignmentAsync(string message, CancellationToken ct)
    {
        var homing = await _onstep.ReturnHomeAsync(ct);
        if (!homing.Succeeded)
        {
            SetStatus("Failed", true, true, homing.Error, homing.Response);
            return new(false, homing.Error ?? "OnStep rejected the Home command.");
        }

        SetStatus("ReturningHome", true, true, message);
        _logger.LogInformation("OnStep alignment start: commanded physical return to Home; waiting for H status");
        return new(true, null);
    }

    private async Task<CalibrationActionResult> StartThreePointAlignmentAtHomeAsync(string message, CancellationToken ct)
    {
        var started = await _onstep.StartAlignmentAsync(3, ct);
        if (!started.Succeeded)
        {
            SetStatus("Failed", true, true, started.Error, started.Response);
            return new(false, started.Error ?? "OnStep did not start the three-point alignment.");
        }

        SetStatus("StartingAlignment", true, true, message, started.Response);
        return await CommandCurrentTargetAsync(ct);
    }

    private async Task SubmitHomeRecoverySolveAsync(SolveResult result, OnStepOptions options, CancellationToken ct)
    {
        if (!result.IsValid || result.Confidence < options.MinSolveConfidence)
        {
            SetStatus("RecoveringHomeSolves", true, true,
                "A usable plate solve is required to recover Home. Retrying.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_firstStableSolve == null)
        {
            _firstStableSolve = result;
            SetStatus("RecoveringHomeSolves", true, true,
                "First recovery solve received; waiting for a second solve to confirm it.");
            return;
        }

        var firstSolve = _firstStableSolve.Value;
        var disagreement = OnStepClient.AngularDistance(
            firstSolve.RaDeg, firstSolve.DecDeg, result.RaDeg, result.DecDeg);
        if (disagreement > options.MaxSolveDisagreementDeg)
        {
            _firstStableSolve = result;
            _nextSolveAttemptAt = now.AddSeconds(options.StableSolveIntervalSeconds);
            SetStatus("RecoveringHomeSolves", true, true,
                $"Recovery solves differed by {disagreement:F3}°. Retrying comparison after {options.StableSolveIntervalSeconds}s.");
            return;
        }

        try
        {
            SetStatus("SyncingRecoveredPosition", true, true,
                "Recovered pointing verified; syncing it to OnStep before returning Home.", candidate: result);
            var sync = await _onstep.SyncSolvedPositionAsync(result, ct);
            if (!sync.Succeeded)
            {
                SetStatus("Failed", true, true, sync.Error, sync.Response, result);
                return;
            }

            _firstStableSolve = null;
            _nextSolveAttemptAt = DateTimeOffset.MinValue;
            var home = await ReturnToHomeBeforeAlignmentAsync(
                "Recovered pointing synced to OnStep; returning mount to Home before point 1.", ct);
            if (!home.Success)
                return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OnStep Home recovery failed");
            RequestConnectionRecovery();
            SetStatus("Failed", false, false, $"Unable to recover Home: {ex.Message}");
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
        _nextMotionPollAt = DateTimeOffset.MinValue;
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
            _simulationEnabled,
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
        new("Idle", false, false, false, "OnStep alignment has not started.", 0, 0, null, null, null, null, null);

    private static bool IsCalibrateMode(string mode) =>
        string.Equals(mode, "calibrate", StringComparison.OrdinalIgnoreCase);

    private static bool IsActive(string state) => state is not ("Idle" or "Completed" or "Aborted" or "Failed");

    private static bool IsSafe(OnStepMountStatus mount) =>
        !mount.IsSlewing && !mount.IsParked && !mount.IsParking && !mount.HasParkFailure &&
        !mount.IsHoming && !mount.IsGuiding && !mount.HasGeneralError;

    private static bool IsSafeOrSlewing(OnStepMountStatus mount) =>
        !mount.IsParked && !mount.IsParking && !mount.IsHoming;

    private void RequestConnectionRecovery()
    {
        _probePending = true;
        _nextProbeAttemptAt = DateTimeOffset.MinValue;
    }

    private void ScheduleNextMotionPoll() =>
        _nextMotionPollAt = DateTimeOffset.UtcNow.AddSeconds(1);

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
