using Microsoft.Extensions.DependencyInjection;
using StepSolve.Solvers;

namespace StepSolve.Tests;

public class BackendSelectionTests
{
    [Theory]
    [InlineData("astrometry", typeof(AstrometrySolver))]
    [InlineData("ASTROMETRY", typeof(AstrometrySolver))]
    [InlineData("cedar", typeof(CedarSolver))]
    [InlineData("Cedar", typeof(CedarSolver))]
    [InlineData("tetra3", typeof(Tetra3Solver))]
    [InlineData("TETRA3", typeof(Tetra3Solver))]
    public void Register_ValidBackend_RegistersCorrectType(string backend, Type expectedType)
    {
        var services = new ServiceCollection();
        SolverRegistration.Register(services, backend);

        var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(ISolver));
        Assert.NotNull(descriptor);
        Assert.Equal(expectedType, descriptor.ImplementationType);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("")]
    [InlineData("platesolve2")]
    [InlineData("astap")]
    public void Register_UnknownBackend_ThrowsInvalidOperationException(string backend)
    {
        var services = new ServiceCollection();
        var ex = Assert.Throws<InvalidOperationException>(() => SolverRegistration.Register(services, backend));
        Assert.Contains("Solver:Backend", ex.Message);
    }

    [Fact]
    public void Register_Unknown_MessageListsValidValues()
    {
        var services = new ServiceCollection();
        var ex = Assert.Throws<InvalidOperationException>(() => SolverRegistration.Register(services, "bogus"));
        Assert.Contains("astrometry", ex.Message);
        Assert.Contains("cedar", ex.Message);
        Assert.Contains("tetra3", ex.Message);
    }
}
