using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

/// <summary>
/// SOLIDWORKS' isometric view faces a direction whose components are 1/√3 only to a few decimals (5.A live run: shown
/// as (0.577, 0.577, 0.577) and refused at 1e-6), so isometric is held to IsometricTolerance and the measured facing is
/// reported to six decimals.
/// </summary>
public class IsometricToleranceTests
{
    [Theory]
    [InlineData(0.57735, 0.57735, 0.57735)]
    [InlineData(0.5774, -0.5773, 0.5773)]
    public void A_facing_within_a_ten_thousandth_of_one_over_root_three_is_isometric(double x, double y, double z)
    {
        Assert.True(ViewGeometry.Isometric(new Vector3(x, y, z)));
    }

    [Theory]
    [InlineData(0.58, 0.57735, 0.57)]
    [InlineData(0.6, 0.6, 0.529)]
    public void A_facing_further_off_is_not(double x, double y, double z)
    {
        Assert.False(ViewGeometry.Isometric(new Vector3(x, y, z)));
    }

    [Fact]
    public void The_measured_facing_is_reported_to_six_decimals()
    {
        var t = Views.Of(new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0.6, 0.6, 0.529150), 1, 0, 0);
        Assert.Contains("(0.600000, 0.600000, 0.529150)", ViewGeometry.OrientationMismatch("isometric", t));
    }
}
