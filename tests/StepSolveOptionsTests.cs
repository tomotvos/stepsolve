using StepSolve;

namespace StepSolve.Tests;

public class StepSolveOptionsTests
{
    [Fact]
    public void StepSolveOptions_HasCorrectDefaults()
    {
        var opts = new StepSolveOptions();
        Assert.Equal("demo", opts.Mode);
        Assert.Equal(5001, opts.WebPort);
        Assert.Equal(5002, opts.Lx200Port);
    }

    [Fact]
    public void SolverOptions_HasCorrectDefaults()
    {
        var opts = new SolverOptions();
        Assert.Equal("astrometry", opts.Backend);
        Assert.Equal(10, opts.HintTimeout);
        Assert.Equal(20.0, opts.SolveRadius);
        Assert.True(opts.EnableFallback);
    }

    [Fact]
    public void AstrometryOptions_HasCorrectDefaults()
    {
        var opts = new AstrometryOptions();
        Assert.Equal("solve-field", opts.SolveFieldPath);
        Assert.Equal("/usr/share/astrometry", opts.IndexPath);
        Assert.Equal(60, opts.Timeout);
        Assert.Equal(5, opts.Sigma);
        Assert.Equal(20, opts.Depth);
    }

    [Fact]
    public void CameraOptions_HasCorrectDefaults()
    {
        var opts = new CameraOptions();
        Assert.Equal(1_000_000, opts.ShutterUs);
        Assert.Equal(8.0, opts.Gain);
        Assert.Equal(1280, opts.Width);
        Assert.Equal(960, opts.Height);
        Assert.Equal("jpg", opts.OutputFormat);
    }

    [Fact]
    public void OnStepOptions_HasCorrectDefaults()
    {
        var opts = new OnStepOptions();
        Assert.False(opts.Enabled);
        Assert.Equal("localhost", opts.Host);
        Assert.Equal(9998, opts.Port);
        Assert.Equal("sync", opts.SyncMode);
        Assert.Equal(5.0, opts.MaxSyncDeltaDeg);
    }

    [Fact]
    public void CedarOptions_HasCorrectDefaults()
    {
        var opts = new CedarOptions();
        Assert.Contains("cedar_solve_service.py", opts.ScriptPath);
        Assert.Contains("cedar-default", opts.IndexPath);
    }

    [Fact]
    public void Tetra3Options_HasCorrectDefaults()
    {
        var opts = new Tetra3Options();
        Assert.Contains("tetra3_solve_service.py", opts.ScriptPath);
        Assert.Contains("tetra3-default", opts.IndexPath);
    }
}
