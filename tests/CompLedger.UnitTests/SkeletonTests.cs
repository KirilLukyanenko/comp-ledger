namespace CompLedger.UnitTests;

public sealed class SkeletonTests
{
    [Fact]
    public void Core_assembly_is_referenced()
    {
        Assert.Equal("CompLedger.Core", typeof(CompLedger.Core.AssemblyMarker).Assembly.GetName().Name);
    }
}
