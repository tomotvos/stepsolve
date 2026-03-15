using StepSolve;

namespace StepSolve.Tests;

public class SolveStateTests
{
    [Fact]
    public void Initial_State_IsIdle()
    {
        var state = new SolveState();
        var (result, timestamp, currentState) = state.Current;

        Assert.Null(result);
        Assert.Equal(default, timestamp);
        Assert.Equal("idle", currentState);
    }

    [Fact]
    public void GetCoordinates_ReturnsZero_WhenNoSolve()
    {
        var state = new SolveState();
        var (ra, dec) = state.GetCoordinates();

        Assert.Equal(0.0, ra);
        Assert.Equal(0.0, dec);
    }

    [Fact]
    public void UpdateResult_StoresResult()
    {
        var state = new SolveState();
        var result = new SolveResult(296.94, 42.69, null, null, 0.95, TimeSpan.FromSeconds(2), "astrometry");

        state.UpdateResult(result);

        var (stored, timestamp, currentState) = state.Current;
        Assert.NotNull(stored);
        Assert.Equal(296.94, stored!.Value.RaDeg);
        Assert.Equal(42.69, stored!.Value.DecDeg);
        Assert.Equal("solved", currentState);
        Assert.NotEqual(default, timestamp);
    }

    [Fact]
    public void GetCoordinates_ReturnsLastSolve()
    {
        var state = new SolveState();
        state.UpdateResult(new SolveResult(180.0, -30.0, null, null, 0.9, TimeSpan.Zero, "test"));

        var (ra, dec) = state.GetCoordinates();
        Assert.Equal(180.0, ra);
        Assert.Equal(-30.0, dec);
    }

    [Fact]
    public void SetState_ChangesState()
    {
        var state = new SolveState();

        state.SetState("capturing");
        Assert.Equal("capturing", state.Current.State);

        state.SetState("solving");
        Assert.Equal("solving", state.Current.State);
    }

    [Fact]
    public void UpdateResult_OverwritesPrevious()
    {
        var state = new SolveState();
        state.UpdateResult(new SolveResult(100, 50, null, null, 0.5, TimeSpan.Zero, "a"));
        state.UpdateResult(new SolveResult(200, -20, null, null, 0.9, TimeSpan.Zero, "b"));

        var (ra, dec) = state.GetCoordinates();
        Assert.Equal(200, ra);
        Assert.Equal(-20, dec);
    }

    [Fact]
    public async Task IsThreadSafe_ConcurrentAccess()
    {
        var state = new SolveState();
        var tasks = new Task[100];

        for (int i = 0; i < 100; i++)
        {
            var val = (double)i;
            tasks[i] = Task.Run(() =>
            {
                state.UpdateResult(new SolveResult(val, val, null, null, 0.9, TimeSpan.Zero, "test"));
                _ = state.GetCoordinates();
                _ = state.Current;
                state.SetState("solving");
            });
        }

        await Task.WhenAll(tasks);

        // Should not throw — just verifying thread safety
        var (ra, dec) = state.GetCoordinates();
        Assert.InRange(ra, 0, 99);
    }
}
