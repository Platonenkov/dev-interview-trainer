namespace Trainer.Domain.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void Solution_Builds()
    {
        Assert.Equal("Trainer.Domain", typeof(AssemblyMarker).Assembly.GetName().Name);
    }
}
