using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using StepSolve.Solvers;

namespace StepSolve.Tests;

public class Tetra3SolverTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"tetra3_tests_{Guid.NewGuid():N}");
    private readonly string _imagePath;

    public Tetra3SolverTests()
    {
        Directory.CreateDirectory(_tempDir);
        _imagePath = Path.Combine(_tempDir, "test.jpg");
        File.WriteAllBytes(_imagePath, [0xFF, 0xD8, 0xFF, 0xE0]);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static string? FindPython()
    {
        foreach (var candidate in new[] { "python3", "python" })
        {
            try
            {
                var psi = new ProcessStartInfo(candidate, "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(3000);
                if (p?.ExitCode == 0) return candidate;
            }
            catch { }
        }
        return null;
    }

    private string WriteStub(string code)
    {
        var path = Path.Combine(_tempDir, $"stub_{Guid.NewGuid():N}.py");
        File.WriteAllText(path, code);
        return path;
    }

    private static Tetra3Solver CreateSolver(string pythonPath, string scriptPath, int timeoutSec = 5)
    {
        var opts = new SolverOptions
        {
            Tetra3 = new Tetra3Options
            {
                PythonPath = pythonPath,
                ScriptPath = scriptPath,
                IndexPath = "",
                Timeout = timeoutSec,
            }
        };
        return new Tetra3Solver(new TestOptionsMonitor<SolverOptions>(opts), NullLogger<Tetra3Solver>.Instance);
    }

    [Fact]
    public async Task SolveAsync_UsesUpdatedFovEstimateWithoutRestartingProcess()
    {
        var python = FindPython();
        if (python == null) return;

        var stub = WriteStub("""
            import sys, json
            print(json.dumps({"ready": True}), flush=True)
            for line in sys.stdin:
                line = line.strip()
                if not line: continue
                fov = json.loads(line).get("fov_estimate_deg")
                print(json.dumps({"ra_deg": 1.0 if fov is None else fov, "dec_deg": 1.0, "confidence": 1.0}), flush=True)
            """);
        var options = new SolverOptions
        {
            FovEstimateDeg = 0,
            Tetra3 = new Tetra3Options
            {
                PythonPath = python,
                ScriptPath = stub,
                IndexPath = "",
                Timeout = 5,
            }
        };
        var monitor = new TestOptionsMonitor<SolverOptions>(options);

        using var solver = new Tetra3Solver(monitor, NullLogger<Tetra3Solver>.Instance);

        var noHint = await solver.SolveAsync(_imagePath, null, CancellationToken.None);
        Assert.Equal(1.0, noHint.RaDeg);

        monitor.Set(new SolverOptions
        {
            FovEstimateDeg = 12.5,
            Tetra3 = options.Tetra3,
        });

        var updatedHint = await solver.SolveAsync(_imagePath, null, CancellationToken.None);
        Assert.Equal(12.5, updatedHint.RaDeg);
    }

    [Fact]
    public async Task SolveAsync_FileNotFound_ThrowsFileNotFoundException()
    {
        var python = FindPython();
        if (python == null) return;

        var stub = WriteStub("""
            import sys, json
            print(json.dumps({"ready": True}), flush=True)
            for line in sys.stdin:
                pass
            """);

        using var solver = CreateSolver(python, stub);
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            solver.SolveAsync("/nonexistent/image.jpg", null, CancellationToken.None));
    }

    [Fact]
    public async Task SolveAsync_RoundTrip_ReturnsValidResult()
    {
        var python = FindPython();
        if (python == null) return;

        var stub = WriteStub("""
            import sys, json
            print(json.dumps({"ready": True}), flush=True)
            for line in sys.stdin:
                line = line.strip()
                if not line: continue
                print(json.dumps({"ra_deg": 180.0, "dec_deg": -45.0, "confidence": 1.0, "solve_time_ms": 8.0}), flush=True)
            """);

        using var solver = CreateSolver(python, stub);
        var result = await solver.SolveAsync(_imagePath, null, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(180.0, result.RaDeg, 1);
        Assert.Equal(-45.0, result.DecDeg, 1);
        Assert.Equal(1.0, result.Confidence, 1);
        Assert.Equal("tetra3", result.SolverName);
    }

    [Fact]
    public async Task SolveAsync_NoSolution_ReturnsInvalidResult()
    {
        var python = FindPython();
        if (python == null) return;

        var stub = WriteStub("""
            import sys, json
            print(json.dumps({"ready": True}), flush=True)
            for line in sys.stdin:
                line = line.strip()
                if not line: continue
                print(json.dumps({"ra_deg": 0.0, "dec_deg": 0.0, "confidence": 0.0, "solve_time_ms": 5.0, "error": "no solution"}), flush=True)
            """);

        using var solver = CreateSolver(python, stub);
        var result = await solver.SolveAsync(_imagePath, null, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("tetra3", result.SolverName);
    }

    [Fact]
    public async Task SolveAsync_ProcessCrashAfterResponse_RestartsOnNextCall()
    {
        var python = FindPython();
        if (python == null) return;

        var stub = WriteStub("""
            import sys, json
            print(json.dumps({"ready": True}), flush=True)
            for line in sys.stdin:
                line = line.strip()
                if not line: continue
                print(json.dumps({"ra_deg": 90.0, "dec_deg": 30.0, "confidence": 0.9, "solve_time_ms": 7.0}), flush=True)
                sys.exit(0)
            """);

        using var solver = CreateSolver(python, stub);

        var result1 = await solver.SolveAsync(_imagePath, null, CancellationToken.None);
        Assert.True(result1.IsValid);

        await Task.Delay(200);

        var result2 = await solver.SolveAsync(_imagePath, null, CancellationToken.None);
        Assert.True(result2.IsValid);
        Assert.Equal("tetra3", result2.SolverName);
    }

    [Fact]
    public async Task SolveAsync_Timeout_ReturnsInvalidResult()
    {
        var python = FindPython();
        if (python == null) return;

        var stub = WriteStub("""
            import sys, json, time
            print(json.dumps({"ready": True}), flush=True)
            for line in sys.stdin:
                time.sleep(60)
            """);

        using var solver = CreateSolver(python, stub, timeoutSec: 1);
        var result = await solver.SolveAsync(_imagePath, null, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("tetra3", result.SolverName);
    }

    [Fact]
    public async Task SolveAsync_MultipleSolves_AllSucceed()
    {
        var python = FindPython();
        if (python == null) return;

        var stub = WriteStub("""
            import sys, json
            print(json.dumps({"ready": True}), flush=True)
            for line in sys.stdin:
                line = line.strip()
                if not line: continue
                print(json.dumps({"ra_deg": 270.0, "dec_deg": 10.0, "confidence": 0.85, "solve_time_ms": 6.0}), flush=True)
            """);

        using var solver = CreateSolver(python, stub);

        for (int i = 0; i < 3; i++)
        {
            var result = await solver.SolveAsync(_imagePath, null, CancellationToken.None);
            Assert.True(result.IsValid, $"Solve {i + 1} should succeed");
            Assert.Equal("tetra3", result.SolverName);
        }
    }
}
