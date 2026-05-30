namespace StepSolve.Tests;

public class UpdateStateTests
{
    [Theory]
    [InlineData("v0.1.1", "v0.1.0", true)]   // patch bump → update
    [InlineData("v0.2.0", "v0.1.0", true)]   // minor bump → update
    [InlineData("v1.0.0", "v0.9.0", true)]   // major bump → update
    [InlineData("v0.1.0", "v0.1.0", false)]  // same → no update
    [InlineData("v0.1.0", "v0.1.1", false)]  // older → no downgrade
    [InlineData("v0.1.0", "v1.0.0", false)]  // older major → no downgrade
    [InlineData("v0.1.1", "dev",    false)]  // dev build → no update
    [InlineData(null,     "v0.1.0", false)]  // null candidate → no update
    [InlineData("",       "v0.1.0", false)]  // empty candidate → no update
    public void IsNewerVersion_ReturnsExpected(string? candidate, string current, bool expected)
    {
        Assert.Equal(expected, UpdateState.IsNewerVersion(candidate, current));
    }
}
