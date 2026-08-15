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
        public int CaptureCount { get; private set; }

        public Task<string?> CaptureAsync(CancellationToken ct)
        {
            CaptureCount++;
            return Task.FromResult(ImageToReturn);
        }
    }

    private sealed class StubSolver : ISolver
    {
        public SolveResult ResultToReturn { get; set; } = new(0, 0, null, null, 0, TimeSpan.Zero, "stub");
        public int SolveCount { get; private set; }
        public string? LastImagePath { get; private set; }

        public Task<SolveResult> SolveAsync(string imagePath, SolveHints? hints, CancellationToken ct)
        {
            SolveCount++;
            LastImagePath = imagePath;
            return Task.FromResult(ResultToReturn);
        }
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

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await service.StartAsync(cts.Token); await Task.Delay(2500, cts.Token); }
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        try { await service.StartAsync(cts.Token); await Task.Delay(2000, cts.Token); }
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        try { await service.StartAsync(cts.Token); await Task.Delay(1500, cts.Token); }
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        try { await service.StartAsync(cts.Token); await Task.Delay(1500, cts.Token); }
        catch (OperationCanceledException) { }
        finally { await service.StopAsync(CancellationToken.None); }

        Assert.Equal(0, camera.CaptureCount);
        Assert.Equal(0, solver.SolveCount);
        Assert.Equal("idle", state.Current.State);
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
