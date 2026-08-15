using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace StepSolve.Solvers;

public sealed class Tetra3Solver : PythonSolverBase
{
    public Tetra3Solver(IOptionsMonitor<SolverOptions> options, ILogger<Tetra3Solver> logger)
        : base(
            options.CurrentValue.Tetra3.PythonPath,
            options.CurrentValue.Tetra3.ScriptPath,
            options.CurrentValue.Tetra3.IndexPath,
            options.CurrentValue.Tetra3.Timeout,
            "tetra3",
            logger,
            () =>
            {
                var fovEstimateDeg = options.CurrentValue.FovEstimateDeg;
                return fovEstimateDeg > 0 ? fovEstimateDeg : null;
            })
    {
    }
}
