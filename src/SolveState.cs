namespace StepSolve;

/// <summary>
/// Thread-safe shared state holding the latest solve result.
/// Read by the LX200 server, HTTP API, and WebSocket broadcaster.
/// Written by the StepSolveService after each successful solve.
/// </summary>
public sealed class SolveState
{
    private readonly Lock _lock = new();
    private SolveResult? _lastResult;
    private DateTimeOffset _lastSolveTime;
    private string _state = "idle"; // idle, capturing, solving, solved, error

    public (SolveResult? Result, DateTimeOffset Timestamp, string State) Current
    {
        get
        {
            lock (_lock)
            {
                return (_lastResult, _lastSolveTime, _state);
            }
        }
    }

    public void UpdateResult(SolveResult result)
    {
        lock (_lock)
        {
            _lastResult = result;
            _lastSolveTime = DateTimeOffset.UtcNow;
            _state = "solved";
        }
    }

    public void SetState(string state)
    {
        lock (_lock)
        {
            _state = state;
        }
    }

    /// <summary>
    /// Get RA/Dec for LX200 server. Returns (0,0) if no solve available.
    /// </summary>
    public (double RaDeg, double DecDeg) GetCoordinates()
    {
        lock (_lock)
        {
            return _lastResult.HasValue
                ? (_lastResult.Value.RaDeg, _lastResult.Value.DecDeg)
                : (0.0, 0.0);
        }
    }
}
