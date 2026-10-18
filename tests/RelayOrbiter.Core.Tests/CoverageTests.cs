namespace RelayOrbiter.Core.Tests;

public class CoverageTests
{
    private const double KerbinR = 600_000.0;

    [Fact]
    public void ChordClearanceIsRCosHalfSpacing() =>
        Precision.Close(3_463_334.0 * Math.Cos(Math.PI / 3), Coverage.ChordClearance(3_463_334.0, 3), "clearance");

    [Theory]
    [InlineData(3, 1_300_000.0, true)]    // r > 2R
    [InlineData(3, 1_100_000.0, false)]   // r < 2R: Kerbin blocks the line
    [InlineData(4, 900_000.0, true)]      // r > R / cos 45° = 848 km
    [InlineData(2, 50_000_000.0, false)]  // opposite satellites always see through the body
    [InlineData(6, 700_000.0, true)]
    public void LineOfSightBetweenNeighbours(int n, double radius, bool visible) =>
        Assert.Equal(visible, Coverage.LineOfSight(radius, KerbinR, n));

    [Fact]
    public void MinimumRadiusForThreeSatellitesIsTwiceTheBodyRadius() =>
        Precision.Close(2 * KerbinR, Coverage.MinRadiusForLineOfSight(KerbinR, 3), "r_min");

    [Fact]
    public void NoRadiusGivesLineOfSightForTwoSatellites() =>
        Assert.True(double.IsPositiveInfinity(Coverage.MinRadiusForLineOfSight(KerbinR, 2)));

    [Theory]
    [InlineData(3, 3)]   // chord = r √3
    [InlineData(6, 1)]   // chord = r
    [InlineData(2, 4)]   // chord = 2r (squared: 4)
    public void NeighbourDistance(int n, double chordSquaredOverR2)
    {
        double r = 1_000_000.0;
        Precision.Close(Math.Sqrt(chordSquaredOverR2) * r, Coverage.NeighbourDistance(r, n), "chord");
    }

    [Fact]
    public void SingleAntennaPowerIsItsOwn() =>
        Assert.Equal(2e9, Coverage.CombinedPower(new[] { (2e9, true, 0.75) }), 3);

    [Fact]
    public void TwoEqualCombinableAntennasFollowKspFormula() =>
        // strongest · (sum / strongest)^exponent = 2e9 · 2^0.75
        Precision.Close(2e9 * Math.Pow(2, 0.75), Coverage.CombinedPower(new[] { (2e9, true, 0.75), (2e9, true, 0.75) }), "power");

    [Fact]
    public void NonCombinableAntennaOnlyCountsWhenStrongest()
    {
        var weakNonCombinable = new[] { (1e10, true, 0.75), (5e9, false, 0.75) };
        Assert.Equal(1e10, Coverage.CombinedPower(weakNonCombinable), 3);
        var strongNonCombinable = new[] { (1e9, true, 0.75), (5e10, false, 0.75) };
        Assert.Equal(5e10, Coverage.CombinedPower(strongNonCombinable), 3);
    }

    [Fact]
    public void NoAntennasNoPower() =>
        Assert.Equal(0.0, Coverage.CombinedPower(Array.Empty<(double, bool, double)>()));

    [Fact]
    public void RangeIsGeometricMeanOfPowers() =>
        Precision.Close(Math.Sqrt(2e9 * 5e10), Coverage.Range(2e9, 5e10), "range");
}
