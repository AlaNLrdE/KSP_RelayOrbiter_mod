namespace RelayOrbiter.Core.Tests;

public class ConstellationShapeTests
{
    private static readonly CelestialBody Kerbin = new KspBodyDatabase().Find("Kerbin")!;
    private const double Sma = 3_463_334.0;

    // ── Slots ─────────────────────────────────────────────────────────────────

    [Fact]
    public void WalkerDeltaSlots()
    {
        // 12:3/1 at 55°: 3 planes 120° apart, 4 per plane 90° apart, planes offset 30°.
        var slots = ConstellationShapes.Slots(ConstellationShapes.WalkerDelta(12, 3, 1, 55, Sma));
        Assert.Equal(12, slots.Count);
        Assert.Equal(new[] { 0.0, 120.0, 240.0 }, slots.Select(s => s.LanOffsetDeg).Distinct());
        Assert.Equal(new[] { 0.0, 90.0, 180.0, 270.0 }, slots.Where(s => s.Plane == 0).Select(s => s.PhaseOffsetDeg));
        Precision.Close(30.0, slots.First(s => s.Plane == 1).PhaseOffsetDeg, "F·360/T");
        Assert.All(slots, s => Assert.Equal(55.0, s.InclinationDeg));
    }

    [Fact]
    public void WalkerStarSpreadsNodesOverHalfACircle() =>
        Assert.Equal(new[] { 0.0, 60.0, 120.0 },
                     ConstellationShapes.Slots(ConstellationShapes.WalkerStar(6, 3, 0, 88, Sma))
                                        .Select(s => s.LanOffsetDeg).Distinct());

    [Fact]
    public void RingIsOnePlaneEvenlySpaced()
    {
        var slots = ConstellationShapes.Slots(ConstellationShapes.Ring(4, Sma));
        Assert.All(slots, s => Assert.Equal(0, s.Plane));
        Assert.Equal(new[] { 0.0, 90.0, 180.0, 270.0 }, slots.Select(s => s.PhaseOffsetDeg));
        Assert.Equal("", ConstellationShapes.Ring(4, Sma).Code());
    }

    [Fact]
    public void PlanesMustSplitTheSatellitesEvenly() =>
        Assert.Throws<ArgumentException>(() => ConstellationShapes.WalkerDelta(10, 3, 1, 55, Sma));

    [Fact]
    public void MolniyaHasHalfTheRotationAndASafePeriapsis()
    {
        double rp = Kerbin.Radius + 100_000;
        var s = ConstellationShapes.Elliptical(ShapeKind.Molniya, 3, Kerbin.Mu, Kerbin.RotPeriod, rp, 63.4);
        Precision.Close(Kerbin.RotPeriod / 2, new OrbitalMechanics().OrbitalPeriod(s.Sma, Kerbin.Mu), "period");
        var slots = ConstellationShapes.Slots(s);
        Precision.Close(rp, slots[0].PeriapsisRadius, "periapsis");
        Assert.True(slots[0].Elliptical);
        Assert.Equal(270.0, slots[0].ArgPeDeg);
        Assert.Equal(new[] { 0.0, 120.0, 240.0 }, slots.Select(x => x.LanOffsetDeg));
        Assert.True(s.OnePerPlane);
    }

    [Fact]
    public void TundraHasTheRotationPeriod()
    {
        var s = ConstellationShapes.Elliptical(ShapeKind.Tundra, 2, Kerbin.Mu, Kerbin.RotPeriod, Kerbin.Radius + 500_000, 63.4);
        Precision.Close(Kerbin.RotPeriod, new OrbitalMechanics().OrbitalPeriod(s.Sma, Kerbin.Mu), "period");
        Assert.Equal("TU63", s.Code());
    }

    [Fact]
    public void TetrahedralIsDraimsFourOrbits()
    {
        var slots = ConstellationShapes.Slots(ConstellationShapes.Tetrahedral(Kerbin.Mu, Kerbin.RotPeriod));
        Assert.Equal(4, slots.Count);
        Assert.Equal(new[] { 0.0, 90.0, 180.0, 270.0 }, slots.Select(s => s.LanOffsetDeg));
        Assert.Equal(new[] { 0.0, 270.0, 180.0, 90.0 }, slots.Select(s => s.PhaseOffsetDeg));
        Assert.All(slots, s => Assert.Equal(ConstellationShapes.DraimEccentricity, s.Eccentricity));
    }

    [Fact]
    public void TrojansAreSixtyDegreesFromTheMoon()
    {
        var two = ConstellationShapes.Slots(ConstellationShapes.Trojans(2, 12e6, 0.0, 0.0, 0.0));
        Assert.Equal(new[] { 60.0, 300.0 }, two.Select(s => s.PhaseOffsetDeg));
        Assert.Equal(180.0, ConstellationShapes.Slots(ConstellationShapes.Trojans(3, 12e6, 0.0, 0.0, 0.0))[2].PhaseOffsetDeg);
    }

    [Theory]
    [InlineData("SYNC WD55-3-1", ShapeKind.WalkerDelta)]
    [InlineData("SYNC WS88-3-0", ShapeKind.WalkerStar)]
    [InlineData("3000K MO63", ShapeKind.Molniya)]
    [InlineData("TU63", ShapeKind.Tundra)]
    [InlineData("8000K TH", ShapeKind.Tetrahedral)]
    [InlineData("12000K TR", ShapeKind.Trojans)]
    public void ShapeComesBackFromTheTag(string tag, ShapeKind kind) =>
        Assert.Equal(kind, ConstellationShapes.KindFromTag(tag));

    [Fact]
    public void RingTagsHaveNoShape()
    {
        Assert.Null(ConstellationShapes.KindFromTag("SYNC i10"));
        Assert.Null(ConstellationShapes.KindFromTag(""));
    }

    [Fact]
    public void WalkerParametersComeBackFromTheTag()
    {
        var spec = ConstellationShapes.WalkerDelta(12, 3, 1, 55, Sma);
        Assert.Equal((55.0, 3, 1), ConstellationShapes.WalkerFromTag("SYNC " + spec.Code()));
    }

    // ── Fit ───────────────────────────────────────────────────────────────────

    // Satellites exactly in a Walker shape with reference node and phase (lan0, theta0).
    private static List<ShapeMember> Perfect(ShapeSpec spec, double lan0, double theta0) =>
        ConstellationShapes.Slots(spec)
            .Select(s => new ShapeMember(s.InclinationDeg, StationKeeping.Mod360(lan0 + s.LanOffsetDeg),
                                         StationKeeping.Mod360(theta0 + s.PhaseOffsetDeg)))
            .ToList();

    [Fact]
    public void FitsAPerfectWalkerConstellation()
    {
        var spec = ConstellationShapes.WalkerDelta(12, 3, 1, 55, Sma);
        var members = Perfect(spec, 37.0, 211.0);
        var fit = ShapeFit.Fit(spec, members);
        Assert.All(fit.PhaseErrorsDeg, e => Assert.True(Math.Abs(e) < 1e-6));
        Assert.All(fit.PlaneErrorsDeg, e => Assert.True(e < 1e-6));
        Assert.Equal(12, fit.SlotOf.Distinct().Count());
    }

    [Fact]
    public void ShuffledSatellitesStillFindTheirSlots()
    {
        var spec = ConstellationShapes.WalkerDelta(6, 2, 1, 45, Sma);
        var members = Perfect(spec, 10.0, 50.0);
        members.Reverse();
        var fit = ShapeFit.Fit(spec, members);
        Assert.All(fit.PhaseErrorsDeg, e => Assert.True(Math.Abs(e) < 1e-6));
    }

    [Fact]
    public void OffSatellitesShowTheirErrors()
    {
        var spec = ConstellationShapes.WalkerDelta(6, 2, 0, 45, Sma);
        var members = Perfect(spec, 0.0, 0.0);
        // One satellite 8° behind its slot, another 3° off its plane.
        members[1] = new ShapeMember(members[1].Plane.InclinationDeg, members[1].Plane.LanDeg, members[1].ArgLatDeg - 8.0);
        members[4] = new ShapeMember(members[4].Plane.InclinationDeg + 3.0, members[4].Plane.LanDeg, members[4].ArgLatDeg);
        var fit = ShapeFit.Fit(spec, members, reference: 0);
        Precision.Close(-8.0, fit.PhaseErrorsDeg[1], "behind");
        Assert.InRange(fit.PlaneErrorsDeg[4], 2.9, 3.1);
        Assert.True(Math.Abs(fit.PhaseErrorsDeg[0]) < 1e-9);
    }

    [Fact]
    public void SatellitesInOnePlaneSplitIntoTheWalkerPlanes()
    {
        // Six satellites all in one ring: three go to each of the two planes.
        var spec = ConstellationShapes.WalkerDelta(6, 2, 0, 45, Sma);
        var members = Enumerable.Range(0, 6).Select(k => new ShapeMember(45, 0, k * 60.0)).ToList();
        var fit = ShapeFit.Fit(spec, members);
        Assert.Equal(3, fit.SlotOf.Count(s => fit.Slots[s].Plane == 0));
        Assert.Equal(3, fit.SlotOf.Count(s => fit.Slots[s].Plane == 1));
        Assert.Equal(6, fit.SlotOf.Distinct().Count());
    }

    [Fact]
    public void FewerSatellitesThanSlots()
    {
        var spec = ConstellationShapes.WalkerDelta(12, 3, 1, 55, Sma);
        var members = Perfect(spec, 0.0, 0.0).Where((_, i) => i % 3 == 0).ToList();
        var fit = ShapeFit.Fit(spec, members);
        Assert.Equal(4, fit.SlotOf.Distinct().Count());
        Assert.All(fit.PhaseErrorsDeg, e => Assert.True(Math.Abs(e) < 1e-6));
    }

    [Fact]
    public void TrojansFollowTheMoon()
    {
        // Moon at θ = 100°: the slots are at 160° and 40°.
        var spec = ConstellationShapes.Trojans(2, 12e6, 0.0, 0.0, 0.0);
        var members = new List<ShapeMember> { new(0, 0, 150.0), new(0, 0, 45.0) };
        var fit = ShapeFit.Fit(spec, members, fixedLan0: 0.0, fixedTheta0: 100.0);
        Precision.Close(-10.0, fit.PhaseErrorsDeg[0], "L4");
        Precision.Close(5.0, fit.PhaseErrorsDeg[1], "L5");
        Precision.Close(100.0, fit.Theta0Deg, "the moon's phase");
    }

    [Fact]
    public void ExcludedSatelliteDoesNotPullTheFit()
    {
        var spec = ConstellationShapes.WalkerDelta(4, 2, 0, 45, Sma);
        var members = Perfect(spec, 0.0, 0.0);
        members[2] = new ShapeMember(members[2].Plane.InclinationDeg, members[2].Plane.LanDeg, members[2].ArgLatDeg + 20.0);
        var fit = ShapeFit.Fit(spec, members, exclude: 2);
        Precision.Close(20.0, fit.PhaseErrorsDeg[2], "excluded");
        Assert.True(Math.Abs(fit.PhaseErrorsDeg[0]) < 1e-6);
    }

    [Fact]
    public void TooManySatellitesIsAnError() =>
        Assert.Throws<ArgumentException>(() => ShapeFit.Fit(ConstellationShapes.Tetrahedral(Kerbin.Mu, Kerbin.RotPeriod),
            Enumerable.Range(0, 5).Select(k => new ShapeMember(31, 0, k)).ToList()));
}
