using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StepSolve.Solvers;

namespace StepSolve;

/// <summary>
/// Background service that runs the capture → solve → publish loop.
/// In demo mode, cycles through bundled demo images and runs the real solver on them.
/// </summary>
public sealed class StepSolveService : BackgroundService
{
    private readonly ICameraCapture _camera;
    private readonly ISolver _solver;
    private readonly SolveState _state;
    private readonly OnStepClient _onstep;
    private readonly WebSocketBroadcaster _ws;
    private readonly IConfiguration _config;
    private readonly ILogger<StepSolveService> _logger;
    private readonly IOnStepCalibrationSession? _calibration;

    public StepSolveService(
        ICameraCapture camera,
        ISolver solver,
        SolveState state,
        OnStepClient onstep,
        WebSocketBroadcaster ws,
        IConfiguration config,
        ILogger<StepSolveService> logger,
        IOnStepCalibrationSession? calibration = null)
    {
        _camera = camera;
        _solver = solver;
        _state = state;
        _onstep = onstep;
        _ws = ws;
        _config = config;
        _logger = logger;
        _calibration = calibration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StepSolve service starting, mode={Mode}", CurrentMode);
        if (_calibration != null)
        {
            try
            {
                await _calibration.InitializeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
        var prevMode = "";

        while (!stoppingToken.IsCancellationRequested)
        {
            var mode = CurrentMode;

            if (mode != prevMode)
            {
                prevMode = mode;
                _ = _ws.BroadcastStatus(mode, _state.Current.State, _onstep);
            }

            try
            {
                switch (mode)
                {
                    case "solve":
                        await RunSolveCycle(stoppingToken);
                        break;
                    case "demo":
                        await RunDemoCycle(stoppingToken);
                        break;
                    case "calibrate":
                        await RunCalibrateCycle(stoppingToken);
                        break;
                    default: // idle
                        break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in solve cycle");
                _state.SetState("error");
            }

            await Task.Delay(mode == "demo" ? 1000 : 500, stoppingToken);
        }

        _logger.LogInformation("StepSolve service stopped");
    }

    private async Task RunSolveCycle(CancellationToken ct)
    {
        // Capture
        _state.SetState("capturing");
        _ = _ws.BroadcastStatus(CurrentMode, "capturing", _onstep);
        var imagePath = await _camera.CaptureAsync(ct);

        if (imagePath == null)
        {
            _logger.LogDebug("No image captured, skipping solve");
            _state.SetState("idle");
            return;
        }

        // Solve
        _state.SetState("solving");
        _ = _ws.BroadcastStatus(CurrentMode, "solving", _onstep);
        var result = await _solver.SolveAsync(imagePath, hints: null, ct);

        if (result.IsValid)
        {
            _state.UpdateResult(result, imagePath);
            _logger.LogInformation(
                "Solved: RA={Ra:F4}° Dec={Dec:F4}° Conf={Conf:F2} Time={Time:F1}s Solver={Solver}",
                result.RaDeg, result.DecDeg, result.Confidence,
                result.SolveTime.TotalSeconds, result.SolverName);

            // Broadcast to WebSocket clients
            _ = _ws.BroadcastSolve(result, hasImage: true);
            _ = _ws.BroadcastStatus(CurrentMode, "solved", _onstep);

            // Automatic mount mutation is intentionally disabled. The default
            // OnStep background policy is read-only validation; model points
            // are added only by the explicit Calibrate-mode approval flow.
        }
        else
        {
            _state.SetState("idle");
            _ = _ws.BroadcastStatus(CurrentMode, "idle", _onstep);
            _logger.LogDebug("Solve returned no result");
        }
    }

    // Bypasses the camera entirely so demo works on any platform, including the RPi without a live sky.
    private async Task RunDemoCycle(CancellationToken ct)
    {
        var demoDir = Path.Combine(AppContext.BaseDirectory, "wwwroot", "demo");
        if (!Directory.Exists(demoDir))
        {
            _logger.LogDebug("Demo directory not found: {Dir}", demoDir);
            return;
        }

        var images = Directory.GetFiles(demoDir, "*.jpg");
        if (images.Length == 0)
        {
            _logger.LogDebug("No demo images found in {Dir}", demoDir);
            return;
        }

        var imagePath = images[Random.Shared.Next(images.Length)];

        _state.SetState("solving");
        _ = _ws.BroadcastStatus(CurrentMode, "solving", _onstep);

        var result = await _solver.SolveAsync(imagePath, hints: null, ct);

        if (result.IsValid)
        {
            _state.UpdateResult(result, imagePath);
            _logger.LogInformation(
                "Demo solved: RA={Ra:F4}° Dec={Dec:F4}° Conf={Conf:F2} Time={Time:F1}s Solver={Solver}",
                result.RaDeg, result.DecDeg, result.Confidence,
                result.SolveTime.TotalSeconds, result.SolverName);
            _ = _ws.BroadcastSolve(result, hasImage: true);
            _ = _ws.BroadcastStatus(CurrentMode, "solved", _onstep);
        }
        else
        {
            _state.SetState("idle");
            _ = _ws.BroadcastStatus(CurrentMode, "idle", _onstep);
            _logger.LogDebug("Demo solve returned no result for {Image}", Path.GetFileName(imagePath));
        }
    }

    // Captures frames continuously for focus/framing; no solver involved.
    // No-op on non-Linux since there is no real camera there.
    private async Task RunCalibrateCycle(CancellationToken ct)
    {
        // The calibration controller advances only through its explicit state
        // machine. It polls an active OnStep GoTo here; a dashboard request
        // alone can never create a solved candidate.
        if (_calibration != null)
            await _calibration.TickAsync(ct);

        if (!OperatingSystem.IsLinux())
        {
            _state.SetState("idle");
            return;
        }

        _state.SetState("capturing");
        _ = _ws.BroadcastStatus(CurrentMode, "capturing", _onstep);
        var imagePath = await _camera.CaptureAsync(ct);

        if (imagePath == null)
        {
            _logger.LogDebug("Calibrate: no image captured");
            _state.SetState("idle");
            return;
        }

        _state.SetImagePath(imagePath);

        // While an approved OnStep calibration session is waiting for fresh
        // plate solves, use this newly captured frame. Normal Calibrate mode
        // remains preview-only and never invokes the solver.
        if (_calibration?.NeedsFreshSolve == true)
        {
            _state.SetState("solving");
            _ = _ws.BroadcastStatus(CurrentMode, "solving", _onstep);
            var result = await _solver.SolveAsync(imagePath, hints: null, ct);
            if (result.IsValid)
            {
                _state.UpdateResult(result, imagePath);
                _ = _ws.BroadcastSolve(result, hasImage: true);
            }
            await _calibration.SubmitFreshSolveAsync(result, ct);
        }

        _state.SetState("idle");
        _ = _ws.BroadcastImage("/solve/image");
        _logger.LogDebug("Calibrate frame captured: {Path}", imagePath);
    }

    private string CurrentMode =>
        (_config["StepSolve:Mode"] ?? "demo").ToLowerInvariant();
}
