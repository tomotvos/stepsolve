namespace StepSolve;

public readonly record struct SolveResult(
    double RaDeg,
    double DecDeg,
    double? RollDeg,
    double? PlateScaleArcsecPerPx,
    double Confidence,
    TimeSpan SolveTime,
    string SolverName
)
{
    public bool IsValid => RaDeg != 0.0 || DecDeg != 0.0;
}

public record SolveHints(double RaDeg, double DecDeg, double RadiusDeg);
