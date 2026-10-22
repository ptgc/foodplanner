namespace Foodplanner.Domain.Tests;

public class SmokeTests
{
    [Fact]
    public void Domain_assembly_is_loadable()
    {
        Assert.Equal("Foodplanner.Domain", typeof(AssemblyMarker).Assembly.GetName().Name);
    }
}
