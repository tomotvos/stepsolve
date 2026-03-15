using StepSolve;

namespace StepSolve.Tests;

public class SolveResultTests
{
    [Fact]
    public void IsValid_ReturnsFalse_WhenBothZero()
    {
        var result = new SolveResult(0, 0, null, null, 0, TimeSpan.Zero, "test");
        Assert.False(result.IsValid);
    }

    [Fact]
    public void IsValid_ReturnsTrue_WhenRaNonZero()
    {
        var result = new SolveResult(180.0, 0, null, null, 0.9, TimeSpan.FromSeconds(1), "test");
        Assert.True(result.IsValid);
    }

    [Fact]
    public void IsValid_ReturnsTrue_WhenDecNonZero()
    {
        var result = new SolveResult(0, 45.0, null, null, 0.9, TimeSpan.FromSeconds(1), "test");
        Assert.True(result.IsValid);
    }

    [Fact]
    public void IsValid_ReturnsTrue_WhenBothNonZero()
    {
        var result = new SolveResult(296.94, 42.69, null, null, 0.95, TimeSpan.FromSeconds(2.3), "astrometry");
        Assert.True(result.IsValid);
    }

    [Fact]
    public void SolveResult_PreservesAllFields()
    {
        var elapsed = TimeSpan.FromMilliseconds(2340);
        var result = new SolveResult(296.94, 42.69, 12.3, 1.5, 0.95, elapsed, "astrometry");

        Assert.Equal(296.94, result.RaDeg);
        Assert.Equal(42.69, result.DecDeg);
        Assert.Equal(12.3, result.RollDeg);
        Assert.Equal(1.5, result.PlateScaleArcsecPerPx);
        Assert.Equal(0.95, result.Confidence);
        Assert.Equal(elapsed, result.SolveTime);
        Assert.Equal("astrometry", result.SolverName);
    }
}
