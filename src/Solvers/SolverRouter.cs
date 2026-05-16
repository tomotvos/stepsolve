using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace StepSolve.Solvers;

internal sealed class SolverRouter : ISolver
{
    private readonly IConfiguration _config;
    private readonly AstrometrySolver _astrometry;
    private readonly CedarSolver _cedar;
    private readonly Tetra3Solver _tetra3;
    private readonly ILogger<SolverRouter> _logger;

    public SolverRouter(
        IConfiguration config,
        AstrometrySolver astrometry,
        CedarSolver cedar,
        Tetra3Solver tetra3,
        ILogger<SolverRouter> logger)
    {
        _config = config;
        _astrometry = astrometry;
        _cedar = cedar;
        _tetra3 = tetra3;
        _logger = logger;
    }

    public Task<SolveResult> SolveAsync(string imagePath, SolveHints? hints, CancellationToken ct)
    {
        var backend = (_config["Solver:Backend"] ?? "astrometry").ToLowerInvariant();
        ISolver solver = backend switch
        {
            "cedar" => _cedar,
            "tetra3" => _tetra3,
            "astrometry" => _astrometry,
            _ => LogAndFallback(backend),
        };
        return solver.SolveAsync(imagePath, hints, ct);
    }

    private ISolver LogAndFallback(string backend)
    {
        _logger.LogWarning("Unknown backend '{Backend}', falling back to astrometry", backend);
        return _astrometry;
    }
}
