using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StepSolve.Solvers;

namespace StepSolve.Tests;

public class BackendSelectionTests
{
    private static ServiceProvider BuildProvider(string backend)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Solver:Backend"] = backend })
            .Build());
        services.AddSingleton<StoragePaths>();
        services.Configure<SolverOptions>(_ => { });
        services.Configure<CameraOptions>(_ => { });
        services.Configure<OnStepOptions>(_ => { });
        SolverRegistration.Register(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Register_RegistersRouterAsISolver()
    {
        var services = new ServiceCollection();
        SolverRegistration.Register(services);

        var descriptor = services.Single(d => d.ServiceType == typeof(ISolver));
        Assert.Equal(typeof(SolverRouter), descriptor.ImplementationType);
    }

    [Fact]
    public void Register_RegistersAllConcreteSolvers()
    {
        var services = new ServiceCollection();
        SolverRegistration.Register(services);

        Assert.Contains(services, d => d.ServiceType == typeof(AstrometrySolver));
        Assert.Contains(services, d => d.ServiceType == typeof(CedarSolver));
        Assert.Contains(services, d => d.ServiceType == typeof(Tetra3Solver));
    }

    [Theory]
    [InlineData("astrometry")]
    [InlineData("ASTROMETRY")]
    [InlineData("cedar")]
    [InlineData("Cedar")]
    [InlineData("tetra3")]
    [InlineData("TETRA3")]
    public async Task SolveAsync_ValidBackend_ThrowsFileNotFoundForMissingImage(string backend)
    {
        using var provider = BuildProvider(backend);
        var solver = provider.GetRequiredService<ISolver>();

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            solver.SolveAsync("/nonexistent/image.jpg", null, CancellationToken.None));
    }
}
