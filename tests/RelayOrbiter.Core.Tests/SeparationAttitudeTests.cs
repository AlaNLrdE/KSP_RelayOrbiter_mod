namespace RelayOrbiter.Core.Tests;

public class SeparationAttitudeTests
{
    [Theory]
    [InlineData(0.0)]    // satellite stacked on the carrier's axis
    [InlineData(30.0)]
    [InlineData(150.0)]  // stacked below (pointing backwards)
    [InlineData(180.0)]
    public void AxialSeparationPointsNormal(double angle) =>
        Assert.Equal(SeparationMode.Normal, SeparationAttitude.Choose(angle));

    [Theory]
    [InlineData(90.0)]   // radially mounted
    [InlineData(60.0)]
    [InlineData(120.0)]
    public void LateralSeparationPointsPrograde(double angle) =>
        Assert.Equal(SeparationMode.Prograde, SeparationAttitude.Choose(angle));

    [Fact]
    public void BoundaryAt45DegreesPicksNormal() =>
        Assert.Equal(SeparationMode.Normal, SeparationAttitude.Choose(45.0));

    [Theory]
    [InlineData(90.0, true)]   // separation axis perpendicular to velocity
    [InlineData(87.5, true)]
    [InlineData(92.9, true)]
    [InlineData(86.0, false)]
    [InlineData(0.0, false)]   // pushing straight along the orbit
    public void ReadyWhenSeparationAxisIsPerpendicularToVelocity(double angleToVelocity, bool ready) =>
        Assert.Equal(ready, SeparationAttitude.IsReady(angleToVelocity));
}
