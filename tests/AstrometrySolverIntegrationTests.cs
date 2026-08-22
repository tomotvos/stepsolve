using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using StepSolve.Solvers;

namespace StepSolve.Tests;

/// <summary>
/// Integration tests that run solve-field against the real demo images.
/// Skipped silently when solve-field is not installed or an image is not present.
/// </summary>
public class AstrometrySolverIntegrationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"astro_int_{Guid.NewGuid():N}");

    public AstrometrySolverIntegrationTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static bool SolveFieldAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("solve-field", "--help")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
            return true;
        }
        catch { return false; }
    }

    private static string? FindDemoImage(string name)
    {
        // Test output directory (wwwroot copied by build)
        var candidate = Path.Combine(AppContext.BaseDirectory, "wwwroot", "demo", name);
        if (File.Exists(candidate)) return candidate;

        // Walk up from test output to find src/wwwroot/demo/
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            candidate = Path.Combine(dir, "src", "wwwroot", "demo", name);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private AstrometrySolver CreateSolver()
    {
        var opts = new SolverOptions { FovEstimateDeg = 0 };
        return new AstrometrySolver(
            new TestOptionsMonitor<SolverOptions>(opts),
            new StoragePaths(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["StepSolve:DataDirectory"] = _tempDir,
                })
                .Build()),
            NullLogger<AstrometrySolver>.Instance);
    }

    [Theory]
    [InlineData("demo.jpg",         296.944646,  42.688983)]
    [InlineData("aquarius.jpg",     348.170122, -17.085088)]
    [InlineData("coronaB.jpg",      239.103715,  23.789141)]
    [InlineData("orionsShield.jpg",  74.795216,   9.344918)]
    public async Task SolveAsync_DemoImage_ReturnsExpectedCoordinates(
        string imageName, double expectedRa, double expectedDec)
    {
        if (!SolveFieldAvailable()) return;

        var sourcePath = FindDemoImage(imageName);
        if (sourcePath == null) return;

        // Solve from a temp copy so artifacts don't pollute the source directory
        var imagePath = Path.Combine(_tempDir, imageName);
        File.Copy(sourcePath, imagePath);

        var solver = CreateSolver();
        var result = await solver.SolveAsync(imagePath, hints: null, CancellationToken.None);

        Assert.True(result.IsValid, $"Solve failed for {imageName}");
        Assert.Equal(expectedRa,  result.RaDeg,  precision: 1);
        Assert.Equal(expectedDec, result.DecDeg, precision: 1);
    }
}
