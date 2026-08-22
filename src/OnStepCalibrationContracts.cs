namespace StepSolve;

/// <summary>
/// Read-only view of an OnStep calibration session for HTTP and dashboard clients.
/// The calibration controller owns all state transitions and safety decisions.
/// </summary>
public sealed record OnStepCalibrationStatus(
    string State,
    bool IsConnected,
    bool IsSafe,
    bool SimulationEnabled,
    string? Message,
    int CurrentPoint,
    int Attempt,
    double? RequestedAzimuthDeg,
    double? RequestedAltitudeDeg,
    double? CandidateRaDeg,
    double? CandidateDecDeg,
    string? LastReply);

/// <summary>
/// Result returned by a requested calibration action. A failed action leaves the
/// current status available to explain why it was rejected.
/// </summary>
public sealed record CalibrationActionResult(bool Success, string? Error);

/// <summary>
/// How a calibration session establishes its mechanical Home reference before
/// starting the three-point alignment.
/// </summary>
public enum CalibrationHomeStrategy
{
    /// <summary>The operator has physically placed the rig at Home.</summary>
    AtHome,

    /// <summary>
    /// OnStep already has trustworthy coordinates, so it can return itself to
    /// Home before the new alignment starts.
    /// </summary>
    ReturnToHome,

    /// <summary>
    /// StepSolve first plate-solves the current arbitrary pointing, Syncs it
    /// to OnStep, then commands a supervised return to Home.
    /// </summary>
    RecoverHome,
}

/// <summary>
/// Boundary between the dashboard/API and the calibration state machine.
/// Implementations must validate connection, mount safety, and operating mode
/// before issuing any OnStep command.
/// </summary>
public interface IOnStepCalibrationController
{
    OnStepCalibrationStatus Status { get; }

    Task<CalibrationActionResult> StartAsync(bool confirmed, CalibrationHomeStrategy homeStrategy, string currentMode, CancellationToken ct);
    Task<CalibrationActionResult> AcceptAsync(string currentMode, CancellationToken ct);
    Task<CalibrationActionResult> AbortAsync(string currentMode, CancellationToken ct);
    Task<CalibrationActionResult> ReconnectAsync(string currentMode, CancellationToken ct);
    Task<CalibrationActionResult> SetSimulationAsync(bool enabled, string currentMode, CancellationToken ct);
}

/// <summary>
/// Internal orchestration surface used by the capture loop. It deliberately
/// accepts only freshly produced solves; dashboard callers cannot manufacture
/// an alignment candidate.
/// </summary>
public interface IOnStepCalibrationSession : IOnStepCalibrationController
{
    bool NeedsFreshSolve { get; }
    bool WantsImmediateFollowUpSolve { get; }
    bool UsesSimulatedSolves { get; }

    Task InitializeAsync(CancellationToken ct);
    Task TickAsync(CancellationToken ct);
    Task SubmitFreshSolveAsync(SolveResult result, CancellationToken ct);
    Task SubmitAutomaticCorrectionCandidateAsync(SolveResult result, CancellationToken ct);
    Task<SolveResult> CreateSimulatedSolveAsync(CancellationToken ct);
}
