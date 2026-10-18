namespace RelayOrbiter.Core.Tests;

public class PlaneGroupingTests
{
    private static OrbitPlane P(double inc, double lan) => new(inc, lan);

    [Fact]
    public void SamePlaneHasNoRelativeInclination() =>
        Assert.True(PlaneGrouping.RelativeInclinationDeg(P(10, 40), P(10, 40)) < 1e-9);

    [Fact]
    public void EquatorialPlanesMatchWhateverTheirNode() =>
        Assert.True(PlaneGrouping.RelativeInclinationDeg(P(0, 12), P(0, 250)) < 1e-9);

    [Fact]
    public void SameInclinationDifferentNodeIsADifferentPlane()
    {
        // 10° inclined, nodes 180° apart: the planes are 20° apart.
        Precision.Close(20.0, PlaneGrouping.RelativeInclinationDeg(P(10, 0), P(10, 180)), "opposite nodes");
        Assert.True(PlaneGrouping.RelativeInclinationDeg(P(10, 0), P(10, 90)) > 10.0);
    }

    [Fact]
    public void SameNodeDifferenceIsTheInclinationDifference() =>
        Precision.Close(7.0, PlaneGrouping.RelativeInclinationDeg(P(10, 30), P(3, 30)), "same node");

    [Fact]
    public void RetrogradeIsNotTheSamePlane() =>
        Precision.Close(180.0, PlaneGrouping.RelativeInclinationDeg(P(0, 0), P(180, 0)), "retrograde");

    [Fact]
    public void GroupsThePlanesWithinTheTolerance()
    {
        var planes = new[] { P(0.8, 223), P(0.0, 0), P(4.5, 100), P(10, 50), P(178, 0) };
        Assert.Equal(new[] { 0, 1, 2 }, PlaneGrouping.SamePlane(planes, 0, 5.0));
        Assert.Equal(new[] { 0, 1, 2, 3 }, PlaneGrouping.SamePlane(planes, 0, 12.0));
    }

    [Fact]
    public void PolarOrbitsNeverJoinAnEquatorialGroup()
    {
        // The case seen in game: equatorial satellites and polar ones with the same node.
        var planes = new[] { P(0.12, 102.45), P(0.09, 139.67), P(90.0, 102.34), P(89.99, 102.41), P(0.08, 199.4) };
        Assert.Equal(new[] { 0, 1, 4 }, PlaneGrouping.SamePlane(planes, 0, 45.0));
        Assert.Equal(new[] { 0, 1, 4 }, PlaneGrouping.SamePlane(planes, 0, 5.0));
    }

    [Fact]
    public void MembersAreWithinTheToleranceOfEachOther()
    {
        // Both 45° from the equatorial seed, on opposite nodes: 90° from each other.
        var planes = new[] { P(0, 0), P(45, 0), P(45, 180) };
        var group = PlaneGrouping.SamePlane(planes, 0, 45.0);
        Assert.Equal(2, group.Count);
        Assert.True(PlaneGrouping.Spread(group.Select(i => planes[i]).ToList()) <= 45.0);
    }

    [Fact]
    public void ClosestPlanesAreTakenFirst()
    {
        // 3° and 9° on opposite sides (12° apart), tolerance 10: the 3° one stays.
        var planes = new[] { P(0, 0), P(9, 180), P(3, 0) };
        Assert.Equal(new[] { 0, 2 }, PlaneGrouping.SamePlane(planes, 0, 10.0));
    }

    [Fact]
    public void FitsChecksEveryMember()
    {
        var planes = new[] { P(0, 0), P(4, 0), P(4, 180) };
        Assert.True(PlaneGrouping.Fits(planes, new[] { 0 }, 2, 5.0));
        Assert.False(PlaneGrouping.Fits(planes, new[] { 0, 1 }, 2, 5.0));
        Precision.Close(8.0, PlaneGrouping.Spread(planes), "spread");
    }

    [Fact]
    public void KeepsTheMembersCallsignWhenTheyShareOne() =>
        Assert.Equal("Orion", PlaneGrouping.ChooseCallsign("Kerbin",
            new[] { "Kerbin Orion 1/3 - SYNC", "Kerbin Orion 3/3 - SYNC" },
            new[] { "Kerbin Aurora 1/2 - 500K" }));

    [Fact]
    public void NewCallsignForMixedOrFreeNames()
    {
        var others = new[] { "Kerbin Aurora 1/2 - 500K" };
        Assert.Equal("Beacon", PlaneGrouping.ChooseCallsign("Kerbin", new[] { "Kerbin Sat 1", "Kerbin Sat 2" }, others));
        Assert.Equal("Beacon", PlaneGrouping.ChooseCallsign("Kerbin",
            new[] { "Kerbin Orion 1/3 - SYNC", "Kerbin Vega 2/3 - SYNC" }, others));
        // Our names around another body don't count.
        Assert.Equal("Beacon", PlaneGrouping.ChooseCallsign("Kerbin", new[] { "Mun Orion 1/3 - 100K" }, others));
    }

    [Fact]
    public void NamesFollowTheOrbitFromTheSeed()
    {
        var names = PlaneGrouping.Names("Mun", "Vega", new[] { 100.0, 10.0, 220.0, 340.0 }, 0, "1800K", 0.4);
        Assert.Equal("Mun Vega 1/4 - 1800K", names[0]);
        Assert.Equal("Mun Vega 2/4 - 1800K", names[2]);
        Assert.Equal("Mun Vega 3/4 - 1800K", names[3]);
        Assert.Equal("Mun Vega 4/4 - 1800K", names[1]);
    }

    [Fact]
    public void NamesCarryTheInclination() =>
        Assert.Equal("Kerbin Vega 1/2 - SYNC i10",
                     PlaneGrouping.Names("Kerbin", "Vega", new[] { 0.0, 180.0 }, 0, "SYNC", 9.8)[0]);

    [Fact]
    public void MeanInclination() =>
        Precision.Close(2.0, PlaneGrouping.MeanInclinationDeg(new[] { P(1, 0), P(3, 0) }), "mean");

    [Fact]
    public void MeanPlaneOfTwoPlanesIsHalfway()
    {
        var mean = PlaneGrouping.MeanPlane(new[] { P(0, 0), P(10, 40) });
        Precision.Close(5.0, PlaneGrouping.RelativeInclinationDeg(mean, P(0, 0)), "to the first");
        Precision.Close(5.0, PlaneGrouping.RelativeInclinationDeg(mean, P(10, 40)), "to the second");
        Precision.Close(40.0, mean.LanDeg, "node");
    }

    [Fact]
    public void MeanPlaneOfOnePlaneIsItself()
    {
        var mean = PlaneGrouping.MeanPlane(new[] { P(28.5, 123) });
        Precision.Close(28.5, mean.InclinationDeg, "inclination");
        Precision.Close(123.0, mean.LanDeg, "node");
    }

    [Fact]
    public void MeanOfEquatorialPlanesIsEquatorial() =>
        Assert.True(PlaneGrouping.MeanPlane(new[] { P(0, 10), P(0, 200) }).InclinationDeg < 1e-9);

    [Fact]
    public void PlaneChangeDvIsTheChordOfTheVelocity()
    {
        Precision.Close(2 * 1000 * Math.Sin(5 * Math.PI / 360), PlaneGrouping.PlaneChangeDv(1000, 5), "5°");
        Precision.Close(1000 * Math.Sqrt(2), PlaneGrouping.PlaneChangeDv(1000, 90), "90°");
    }
}
