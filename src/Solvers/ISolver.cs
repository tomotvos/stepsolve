namespace StepSolve.Solvers;

public interface ISolver
{
    Task<SolveResult> SolveAsync(string imagePath, SolveHints? hints, CancellationToken ct);
}
