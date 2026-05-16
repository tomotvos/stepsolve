using Microsoft.Extensions.Logging.Abstractions;
using StepSolve.Solvers;

namespace StepSolve.Tests;

/// <summary>
/// Integration test that runs the real tetra3 service against demo.jpg.
/// Skipped silently when Python, the script, the index, or the image are absent.
/// </summary>
public class Tetra3SolverIntegrationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"tetra3_int_{Guid.NewGuid():N}");

    // Dev-environment paths from appsettings.Development.json
    private const string PythonPath = "/Users/tomo/.stepsolve-dev-venv/bin/python";
    private const string ScriptPath = "/Users/tomo/Development/solve/stepsolve/scripts/tetra3_solve_service.py";
    private const string IndexPath  = "/Users/tomo/.stepsolve-dev-venv/lib/python3.13/site-packages/tetra3/data/default_database";

    public Tetra3SolverIntegrationTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static bool PrerequisitesAvailable() =>
        File.Exists(PythonPath) && File.Exists(ScriptPath) &&
        (Directory.Exists(IndexPath) || File.Exists(IndexPath + ".npz"));

    private static string? FindDemoImage(string name)
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "wwwroot", "demo", name);
        if (File.Exists(candidate)) return candidate;

        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            candidate = Path.Combine(dir, "src", "wwwroot", "demo", name);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private Tetra3Solver CreateSolver()
    {
        var opts = new SolverOptions
        {
            FovEstimateDeg = 0,  // no hint — demo images are from mixed hardware
            Tetra3 = new Tetra3Options
            {
                PythonPath = PythonPath,
                ScriptPath = ScriptPath,
                IndexPath  = IndexPath,
                Timeout    = 60,
            },
        };
        return new Tetra3Solver(
            new TestOptionsMonitor<SolverOptions>(opts),
            NullLogger<Tetra3Solver>.Instance);
    }

    [Fact]
    public async Task SolveAsync_DemoJpg_ReturnsExpectedCoordinates()
    {
        if (!PrerequisitesAvailable()) return;

        var sourcePath = FindDemoImage("demo.jpg");
        if (sourcePath == null) return;

        var imagePath = Path.Combine(_tempDir, "demo.jpg");
        File.Copy(sourcePath, imagePath);

        using var solver = CreateSolver();
        var result = await solver.SolveAsync(imagePath, hints: null, CancellationToken.None);

        Assert.True(result.IsValid, "Tetra3 solve failed for demo.jpg");
        Assert.Equal(296.944646, result.RaDeg,  precision: 0);
        Assert.Equal(42.688983,  result.DecDeg, precision: 0);
    }
}
