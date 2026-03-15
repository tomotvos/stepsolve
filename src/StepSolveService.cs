using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StepSolve.Solvers;

namespace StepSolve;

/// <summary>
/// Background service that runs the capture → solve → publish loop.
/// In demo mode, generates a simulated RA/Dec sweep.
/// </summary>
public sealed class StepSolveService : BackgroundService
{
    private readonly ICameraCapture _camera;
    private readonly ISolver _solver;
    private readonly SolveState _state;
    private readonly OnStepClient _onstep;
    private readonly WebSocketBroadcaster _ws;
    private readonly IOptionsMonitor<StepSolveOptions> _options;
    private readonly ILogger<StepSolveService> _logger;

    // Demo sweep state
    private double _demoRa;
    private double _demoDec = 45.0;

    public StepSolveService(
        ICameraCapture camera,
        ISolver solver,
        SolveState state,
        OnStepClient onstep,
        WebSocketBroadcaster ws,
        IOptionsMonitor<StepSolveOptions> options,
        ILogger<StepSolveService> logger)
    {
        _camera = camera;
        _solver = solver;
        _state = state;
        _onstep = onstep;
        _ws = ws;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StepSolve service starting, mode={Mode}", _options.CurrentValue.Mode);

        while (!stoppingToken.IsCancellationRequested)
        {
            var mode = _options.CurrentValue.Mode.ToLowerInvariant();

            try
            {
                switch (mode)
                {
                    case "solve":
                        await RunSolveCycle(stoppingToken);
                        break;
                    case "demo":
                        RunDemoCycle();
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
        _ = _ws.BroadcastStatus(_options.CurrentValue.Mode, "capturing", _onstep);
        var imagePath = await _camera.CaptureAsync(ct);

        if (imagePath == null)
        {
            _logger.LogDebug("No image captured, skipping solve");
            _state.SetState("idle");
            return;
        }

        // Solve
        _state.SetState("solving");
        _ = _ws.BroadcastStatus(_options.CurrentValue.Mode, "solving", _onstep);
        var result = await _solver.SolveAsync(imagePath, hints: null, ct);

        if (result.IsValid)
        {
            _state.UpdateResult(result);
            _logger.LogInformation(
                "Solved: RA={Ra:F4}° Dec={Dec:F4}° Conf={Conf:F2} Time={Time:F1}s Solver={Solver}",
                result.RaDeg, result.DecDeg, result.Confidence,
                result.SolveTime.TotalSeconds, result.SolverName);

            // Broadcast to WebSocket clients
            _ = _ws.BroadcastSolve(result);
            _ = _ws.BroadcastStatus(_options.CurrentValue.Mode, "solved", _onstep);

            // Sync to OnStep (fire-and-forget — does not block solve loop)
            _ = _onstep.SyncAsync(result, _state, ct);
        }
        else
        {
            _state.SetState("idle");
            _ = _ws.BroadcastStatus(_options.CurrentValue.Mode, "idle", _onstep);
            _logger.LogDebug("Solve returned no result");
        }
    }

    private void RunDemoCycle()
    {
        // Simulate a smooth RA sweep (full circle in ~6 minutes)
        _demoRa = (_demoRa + 1.0) % 360.0;

        var result = new SolveResult(
            RaDeg: _demoRa,
            DecDeg: _demoDec,
            RollDeg: null,
            PlateScaleArcsecPerPx: null,
            Confidence: 0.99,
            SolveTime: TimeSpan.FromMilliseconds(12),
            SolverName: "demo"
        );

        _state.UpdateResult(result);
        _ = _ws.BroadcastSolve(result);
        _ = _ws.BroadcastStatus(_options.CurrentValue.Mode, "solved", _onstep);
    }
}
