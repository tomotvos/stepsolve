using Microsoft.Extensions.DependencyInjection;

namespace StepSolve.Solvers;

internal static class SolverRegistration
{
    internal static void Register(IServiceCollection services)
    {
        services.AddSingleton<AstrometrySolver>();
        services.AddSingleton<CedarSolver>();
        services.AddSingleton<Tetra3Solver>();
        services.AddSingleton<ISolver, SolverRouter>();
    }
}
