using StepSolve;
using Microsoft.Extensions.Configuration;

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
        Assert.Equal("tetra3", opts.Backend);
        Assert.Equal(10, opts.HintTimeout);
        Assert.Equal(20.0, opts.SolveRadius);
        Assert.True(opts.EnableFallback);
    }

    [Fact]
    public void AstrometryOptions_HasCorrectDefaults()
    {
        var opts = new AstrometryOptions();
        Assert.Equal("solve-field", opts.SolveFieldPath);
        Assert.Equal("", opts.IndexPath);
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
        Assert.Equal(10, opts.CommandTimeoutSeconds);
        Assert.Equal("sync", opts.SyncMode);
        Assert.Equal(5.0, opts.MaxSyncDeltaDeg);
        Assert.Equal("probe", opts.StartupPolicy);
        Assert.Equal("validate", opts.BackgroundPolicy);
        Assert.False(opts.AutomaticCorrectionsEnabled);
        Assert.Equal(15, opts.CorrectionIntervalMinutes);
        Assert.Equal(1.0, opts.MaxAutomaticCorrectionDeg);
        Assert.Equal(1, opts.StableSolveIntervalSeconds);
        Assert.Equal(3, opts.CalibrationSettleSeconds);
        Assert.Empty(opts.CalibrationTargets);
    }

    [Fact]
    public void OnStepOptions_ConfigurationBindingUsesConfiguredTargetsWithoutDuplication()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OnStep:CalibrationTargets:0:AzimuthDeg"] = "0",
                ["OnStep:CalibrationTargets:0:AltitudeDeg"] = "45",
                ["OnStep:CalibrationTargets:1:AzimuthDeg"] = "60",
                ["OnStep:CalibrationTargets:1:AltitudeDeg"] = "60",
                ["OnStep:CalibrationTargets:2:AzimuthDeg"] = "90",
                ["OnStep:CalibrationTargets:2:AltitudeDeg"] = "80",
            })
            .Build();

        var opts = config.GetSection(OnStepOptions.Section).Get<OnStepOptions>();

        Assert.NotNull(opts);
        Assert.Collection(opts.CalibrationTargets,
            target => Assert.Equal(new OnStepCalibrationTarget(0, 45), target),
            target => Assert.Equal(new OnStepCalibrationTarget(60, 60), target),
            target => Assert.Equal(new OnStepCalibrationTarget(90, 80), target));
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
