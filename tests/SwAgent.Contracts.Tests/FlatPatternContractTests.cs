using Xunit;

namespace SwAgent.Contracts.Tests;

public class FlatPatternContractTests
{
    [Fact]
    public void Protocol_10_speaks_flat_pattern_export()
    {
        Assert.Equal(10, Protocol.Version);
    }
}
