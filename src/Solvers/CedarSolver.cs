using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace StepSolve.Solvers;

public sealed class CedarSolver : PythonSolverBase
{
    public CedarSolver(IOptionsMonitor<SolverOptions> options, ILogger<CedarSolver> logger)
        : base(
            options.CurrentValue.Cedar.PythonPath,
            options.CurrentValue.Cedar.ScriptPath,
            options.CurrentValue.Cedar.IndexPath,
            options.CurrentValue.Cedar.Timeout,
            "cedar",
            logger)
    {
    }
}
