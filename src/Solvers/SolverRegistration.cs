using Microsoft.Extensions.DependencyInjection;

namespace StepSolve.Solvers;

internal static class SolverRegistration
{
    internal static void Register(IServiceCollection services, string backend)
    {
        switch (backend.ToLowerInvariant())
        {
            case "astrometry":
                services.AddSingleton<ISolver, AstrometrySolver>();
                break;
            case "cedar":
                services.AddSingleton<ISolver, CedarSolver>();
                break;
            case "tetra3":
                services.AddSingleton<ISolver, Tetra3Solver>();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown Solver:Backend value '{backend}'. Valid values: astrometry, cedar, tetra3");
        }
    }
}
