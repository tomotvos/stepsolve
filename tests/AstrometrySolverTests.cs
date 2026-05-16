using StepSolve.Solvers;

namespace StepSolve.Tests;

public class AstrometrySolverTests
{
    [Fact]
    public void BuildArgs_BasicNoHints()
    {
        var opts = new AstrometryOptions();
        var args = AstrometrySolver.BuildArgs("/tmp/test.jpg", opts, hints: null, keepXyPath: null);

        Assert.Contains("/tmp/test.jpg", args);
        Assert.Contains("--overwrite", args);
        Assert.Contains("--no-plots", args);
        Assert.Contains("--sigma 5", args);
        Assert.Contains("--depth 20", args);
        Assert.Contains("--uniformize 0", args);
        Assert.Contains("--no-remove-lines", args);
        Assert.Contains("--match none", args);
        Assert.DoesNotContain("--index-dir", args);
        Assert.DoesNotContain("--ra", args);
        Assert.DoesNotContain("--dec", args);
    }

    [Fact]
    public void BuildArgs_WithCustomSigmaAndDepth()
    {
        var opts = new AstrometryOptions { Sigma = 10, Depth = 40 };
        var args = AstrometrySolver.BuildArgs("/tmp/test.jpg", opts, hints: null, keepXyPath: null);

        Assert.Contains("--sigma 10", args);
        Assert.Contains("--depth 40", args);
    }

    [Fact]
    public void BuildArgs_WithHints()
    {
        var opts = new AstrometryOptions();
        var hints = new SolveHints(296.94, 42.69, 20.0);
        var args = AstrometrySolver.BuildArgs("/tmp/test.jpg", opts, hints, keepXyPath: null);

        Assert.Contains("--ra 296.940000", args);
        Assert.Contains("--dec 42.690000", args);
        Assert.Contains("--radius 20.0", args);
    }

    [Fact]
    public void BuildArgs_WithXyPath()
    {
        var opts = new AstrometryOptions();
        var args = AstrometrySolver.BuildArgs("/tmp/test.jpg", opts, hints: null, "/tmp/test.xy");

        Assert.Contains("--keep-xylist /tmp/test.xy", args);
    }

    [Fact]
    public void BuildArgs_EmptyIndexPath_OmitsFlag()
    {
        var opts = new AstrometryOptions { IndexPath = "" };
        var args = AstrometrySolver.BuildArgs("/tmp/test.jpg", opts, hints: null, keepXyPath: null);

        Assert.DoesNotContain("--index-dir", args);
    }

    [Fact]
    public void BuildArgs_WithCustomIndexPath()
    {
        var opts = new AstrometryOptions { IndexPath = "/opt/astrometry/indexes" };
        var args = AstrometrySolver.BuildArgs("/tmp/test.jpg", opts, hints: null, keepXyPath: null);

        Assert.Contains("--index-dir /opt/astrometry/indexes", args);
    }

    [Fact]
    public void BuildArgs_WithFovEstimate_AddsScaleFlags()
    {
        var opts = new AstrometryOptions();
        var args = AstrometrySolver.BuildArgs("/tmp/test.jpg", opts, hints: null, keepXyPath: null, fovEstimateDeg: 27.7);

        Assert.Contains("--scale-low 13.85", args);
        Assert.Contains("--scale-high 41.55", args);
        Assert.Contains("--scale-units degwidth", args);
    }

    [Fact]
    public void BuildArgs_NoFovEstimate_OmitsScaleFlags()
    {
        var opts = new AstrometryOptions();
        var args = AstrometrySolver.BuildArgs("/tmp/test.jpg", opts, hints: null, keepXyPath: null, fovEstimateDeg: null);

        Assert.DoesNotContain("--scale-low", args);
        Assert.DoesNotContain("--scale-high", args);
    }

    [Fact]
    public void ParseOutput_ExtractsRaDec_PrimaryFormat()
    {
        var output = """
            [10:42:02] Reading input file 1 of 1: /tmp/test.jpg
            [10:42:02] RA,Dec = (296.944646, 42.688983) deg.
            [10:42:02] Total solve time: 0.99 seconds.
            """;

        var result = AstrometrySolver.ParseOutput(output, TimeSpan.FromSeconds(1));

        Assert.Equal(296.944646, result.RaDeg, precision: 6);
        Assert.Equal(42.688983, result.DecDeg, precision: 6);
        Assert.True(result.IsValid);
        Assert.Equal("astrometry", result.SolverName);
    }

    [Fact]
    public void ParseOutput_ExtractsRaDec_FallbackFormat()
    {
        var output = """
            Field center: (RA,Dec) = (180.123456, -45.678901) deg.
            Field size: 22.849 x 17.2545 degrees
            """;

        var result = AstrometrySolver.ParseOutput(output, TimeSpan.FromSeconds(2));

        Assert.Equal(180.123456, result.RaDeg, precision: 6);
        Assert.Equal(-45.678901, result.DecDeg, precision: 6);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void ParseOutput_ExtractsConfidence()
    {
        var output = """
            RA,Dec = (100.0, 50.0) deg.
            Confidence: 0.95
            """;

        var result = AstrometrySolver.ParseOutput(output, TimeSpan.FromSeconds(1));

        Assert.Equal(0.95, result.Confidence, precision: 2);
    }

    [Fact]
    public void ParseOutput_ReturnsZero_WhenNoMatch()
    {
        var output = "No solution found.\nDone.";

        var result = AstrometrySolver.ParseOutput(output, TimeSpan.FromSeconds(5));

        Assert.Equal(0, result.RaDeg);
        Assert.Equal(0, result.DecDeg);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void ParseOutput_HandlesNegativeDec()
    {
        var output = "RA,Dec = (0.5, -89.999) deg.";

        var result = AstrometrySolver.ParseOutput(output, TimeSpan.FromSeconds(1));

        Assert.Equal(0.5, result.RaDeg, precision: 3);
        Assert.Equal(-89.999, result.DecDeg, precision: 3);
    }

    [Fact]
    public void ParseOutput_StripsTimestamps()
    {
        var output = "[12:34:56] RA,Dec = (10.0, 20.0) deg.";

        var result = AstrometrySolver.ParseOutput(output, TimeSpan.FromSeconds(1));

        Assert.Equal(10.0, result.RaDeg);
        Assert.Equal(20.0, result.DecDeg);
    }

    [Fact]
    public void ParseOutput_PreservesSolveTime()
    {
        var output = "RA,Dec = (10.0, 20.0) deg.";
        var elapsed = TimeSpan.FromMilliseconds(2340);

        var result = AstrometrySolver.ParseOutput(output, elapsed);

        Assert.Equal(elapsed, result.SolveTime);
    }

    [Fact]
    public void ParseOutput_HandlesFullAstrometryOutput()
    {
        var output = """
            [10:42:00] Reading input file 1 of 1: /tmp/capture.jpg
            [10:42:00] Extracting sources...
            [10:42:00] Extracted 150 sources.
            [10:42:01] Solving...
            [10:42:02] Field center: (RA,Dec) = (296.944646, 42.688983) deg.
            [10:42:02] Field size: 22.849 x 17.2545 degrees
            [10:42:02] Field rotation angle: up is -165.998 degrees E of N
            [10:42:02] Field parity: neg
            [10:42:02] Test image solved. Total solve time: 0.99 seconds.
            """;

        var result = AstrometrySolver.ParseOutput(output, TimeSpan.FromSeconds(2));

        Assert.Equal(296.944646, result.RaDeg, precision: 6);
        Assert.Equal(42.688983, result.DecDeg, precision: 6);
        Assert.True(result.IsValid);
    }
}
