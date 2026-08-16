using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StepSolve;
using StepSolve.Solvers;

namespace StepSolve.Tests;

/// <summary>
/// Tests for StepSolveService (the BackgroundService).
/// Uses stubs for camera and solver to test the orchestration logic.
/// </summary>
public class StepSolveServiceTests
{
    private sealed class StubCamera : ICameraCapture
    {
        public string? ImageToReturn { get; set; }
        private int _captureCount;
        private readonly TaskCompletionSource _captured = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CaptureCount => Volatile.Read(ref _captureCount);

        public Task<string?> CaptureAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _captureCount);
            _captured.TrySetResult();
            return Task.FromResult(ImageToReturn);
        }

        public Task WaitForCaptureAsync(CancellationToken ct) => _captured.Task.WaitAsync(ct);
    }

    private sealed class StubSolver : ISolver
    {
        public SolveResult ResultToReturn { get; set; } = new(0, 0, null, null, 0, TimeSpan.Zero, "stub");
        private int _solveCount;
        private readonly TaskCompletionSource _solved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SolveCount => Volatile.Read(ref _solveCount);
        public string? LastImagePath { get; private set; }

        public Task<SolveResult> SolveAsync(string imagePath, SolveHints? hints, CancellationToken ct)
        {
            Interlocked.Increment(ref _solveCount);
            LastImagePath = imagePath;
            _solved.TrySetResult();
            return Task.FromResult(ResultToReturn);
        }

        public Task WaitForSolveAsync(CancellationToken ct) => _solved.Task.WaitAsync(ct);
    }

    private sealed class StubCalibration : IOnStepCalibrationSession
    {
        private readonly TaskCompletionSource _submitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SimulatedSolveCount { get; private set; }
        public SolveResult? Submitted { get; private set; }

        public OnStepCalibrationStatus Status { get; private set; } = new(
            "AwaitingStableSolves", true, true, true, "simulation", 1, 1, 0, 45, null, null, null);
        public bool NeedsFreshSolve => true;
        public bool UsesSimulatedSolves => true;

        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task TickAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<CalibrationActionResult> StartAsync(bool confirmed, string currentMode, CancellationToken ct) => Task.FromResult(new CalibrationActionResult(true, null));
        public Task<CalibrationActionResult> AcceptAsync(string currentMode, CancellationToken ct) => Task.FromResult(new CalibrationActionResult(true, null));
        public Task<CalibrationActionResult> AbortAsync(string currentMode, CancellationToken ct) => Task.FromResult(new CalibrationActionResult(true, null));
        public Task<CalibrationActionResult> SetSimulationAsync(bool enabled, string currentMode, CancellationToken ct) => Task.FromResult(new CalibrationActionResult(true, null));

        public Task<SolveResult> CreateSimulatedSolveAsync(CancellationToken ct)
        {
            SimulatedSolveCount++;
            return Task.FromResult(new SolveResult(180, 45, null, null, 0.99, TimeSpan.Zero, "onstep-simulation"));
        }

        public Task SubmitFreshSolveAsync(SolveResult result, CancellationToken ct)
        {
            Submitted = result;
            _submitted.TrySetResult();
            return Task.CompletedTask;
        }

        public Task WaitForSubmissionAsync(CancellationToken ct) => _submitted.Task.WaitAsync(ct);
    }

    private static IConfiguration CreateConfig(string mode = "solve")
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["StepSolve:Mode"] = mode })
            .Build();
    }

    private static OnStepClient CreateOnStepClient()
    {
        var opts = new TestOptionsMonitor<OnStepOptions>(new OnStepOptions { Enabled = false });
        return new OnStepClient(opts, NullLogger<OnStepClient>.Instance);
    }

    [Fact]
    public async Task DemoMode_CallsSolverWithDemoImage_NotCamera()
    {
        var demoDir = Path.Combine(AppContext.BaseDirectory, "wwwroot", "demo");
        Directory.CreateDirectory(demoDir);
        var demoImage = Path.Combine(demoDir, $"test_{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(demoImage, [0xFF, 0xD8, 0xFF, 0xE0]); // minimal JPEG header

        try
        {
            var state = new SolveState();
            var camera = new StubCamera();
            var solver = new StubSolver
            {
                ResultToReturn = new SolveResult(180.0, 45.0, null, null, 0.95, TimeSpan.FromMilliseconds(200), "tetra3")
            };
            var service = new StepSolveService(camera, solver, state, CreateOnStepClient(), new WebSocketBroadcaster(),
                CreateConfig("demo"), NullLogger<StepSolveService>.Instance);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await service.StartAsync(cts.Token); await solver.WaitForSolveAsync(cts.Token); }
            catch (OperationCanceledException) { }
            finally { await service.StopAsync(CancellationToken.None); }

            Assert.Equal(0, camera.CaptureCount);
            Assert.True(solver.SolveCount > 0, "Solver should have been called with demo image");
            Assert.StartsWith(demoDir, solver.LastImagePath);

            var (result, _, _) = state.Current;
            Assert.NotNull(result);
            Assert.Equal(180.0, result!.Value.RaDeg);
            Assert.Equal("tetra3", result!.Value.SolverName);
        }
        finally
        {
            if (File.Exists(demoImage)) File.Delete(demoImage);
        }
    }

    [Fact]
    public async Task SolveMode_CallsCameraAndSolver()
    {
        var state = new SolveState();
        var camera = new StubCamera { ImageToReturn = "/tmp/test.jpg" };
        var solver = new StubSolver
        {
            ResultToReturn = new SolveResult(100, 50, null, null, 0.9, TimeSpan.FromSeconds(1), "test")
        };
        var service = new StepSolveService(camera, solver, state, CreateOnStepClient(), new WebSocketBroadcaster(),
            CreateConfig("solve"), NullLogger<StepSolveService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            await service.StartAsync(cts.Token);
            await camera.WaitForCaptureAsync(cts.Token);
            await solver.WaitForSolveAsync(cts.Token);
        }
        catch (OperationCanceledException) { }
        finally { await service.StopAsync(CancellationToken.None); }

        Assert.True(camera.CaptureCount > 0, "Camera should have been called");
        Assert.True(solver.SolveCount > 0, "Solver should have been called");
        Assert.Equal("/tmp/test.jpg", solver.LastImagePath);

        var (result, _, currentState) = state.Current;
        Assert.NotNull(result);
        Assert.Equal(100, result!.Value.RaDeg);
        Assert.Equal("solved", currentState);
    }

    [Fact]
    public async Task SolveMode_SkipsSolve_WhenNoCapturedImage()
    {
        var state = new SolveState();
        var camera = new StubCamera { ImageToReturn = null };
        var solver = new StubSolver();
        var service = new StepSolveService(camera, solver, state, CreateOnStepClient(), new WebSocketBroadcaster(),
            CreateConfig("solve"), NullLogger<StepSolveService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try { await service.StartAsync(cts.Token); await camera.WaitForCaptureAsync(cts.Token); }
        catch (OperationCanceledException) { }
        finally { await service.StopAsync(CancellationToken.None); }

        Assert.True(camera.CaptureCount > 0, "Camera should have been called");
        Assert.Equal(0, solver.SolveCount);
    }

    [Fact]
    public async Task IdleMode_DoesNothing()
    {
        var state = new SolveState();
        var camera = new StubCamera();
        var solver = new StubSolver();
        var service = new StepSolveService(camera, solver, state, CreateOnStepClient(), new WebSocketBroadcaster(),
            CreateConfig("idle"), NullLogger<StepSolveService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try { await service.StartAsync(cts.Token); await Task.Delay(1500, cts.Token); }
        catch (OperationCanceledException) { }
        finally { await service.StopAsync(CancellationToken.None); }

        Assert.Equal(0, camera.CaptureCount);
        Assert.Equal(0, solver.SolveCount);
        Assert.Equal("idle", state.Current.State);
    }

    [Fact]
    public async Task CalibrateMode_WithSimulation_UsesNoCameraOrSolver()
    {
        var state = new SolveState();
        var camera = new StubCamera();
        var solver = new StubSolver();
        var calibration = new StubCalibration();
        var service = new StepSolveService(camera, solver, state, CreateOnStepClient(), new WebSocketBroadcaster(),
            CreateConfig("calibrate"), NullLogger<StepSolveService>.Instance, calibration);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await service.StartAsync(cts.Token);
            await calibration.WaitForSubmissionAsync(cts.Token);
        }
        finally { await service.StopAsync(CancellationToken.None); }

        Assert.Equal(0, camera.CaptureCount);
        Assert.Equal(0, solver.SolveCount);
        Assert.True(calibration.SimulatedSolveCount > 0);
        Assert.Equal("onstep-simulation", calibration.Submitted?.SolverName);
    }
}

/// <summary>
/// Simple IOptionsMonitor implementation for testing.
/// </summary>
internal sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
{
    public TestOptionsMonitor(T currentValue) => CurrentValue = currentValue;
    public T CurrentValue { get; private set; }
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
    public void Set(T currentValue) => CurrentValue = currentValue;
}
