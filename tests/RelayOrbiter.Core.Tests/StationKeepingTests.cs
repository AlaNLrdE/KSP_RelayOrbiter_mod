namespace RelayOrbiter.Core.Tests;

public class StationKeepingTests
{
    private static readonly CelestialBody Kerbin = new KspBodyDatabase().Find("Kerbin")!;
    private readonly OrbitalMechanics _mech = new();
    private readonly PhasingPlanner _phasing = new(new OrbitalMechanics());
    private static double R(double altitude) => Kerbin.Radius + altitude;

    // ── Names ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ParsesOurNames()
    {
        var n = ConstellationName.Parse("Kerbin Orion 2/6 - SYNC i10")!;
        Assert.Equal("Kerbin", n.Body);
        Assert.Equal("Orion", n.Callsign);
        Assert.Equal(2, n.Index);
        Assert.Equal(6, n.Count);
        Assert.Equal("SYNC i10", n.Tag);
    }

    [Fact]
    public void ParsesSecondSeriesCallsignsAndBodiesWithSpaces()
    {
        var n = ConstellationName.Parse("Sarnus Moon Aurora II 3/4 - 1000K")!;
        Assert.Equal("Sarnus Moon", n.Body);
        Assert.Equal("Aurora II", n.Callsign);
        Assert.Equal(3, n.Index);
    }

    [Theory]
    [InlineData("Kerbin Sat 1")]
    [InlineData("Kerbin Orion 7/6 - SYNC")]   // index beyond the size
    [InlineData("Kerbin Orion 0/6 - SYNC")]
    [InlineData("Kerbin Frodo 1/3 - SYNC")]   // not one of our callsigns
    public void RejectsOtherNames(string name) => Assert.Null(ConstellationName.Parse(name));

    [Fact]
    public void SuggestsTheGroupFromOurNames()
    {
        var g = ConstellationGroup.Suggest("Kerbin Orion 2/4 - SYNC", new[]
        {
            "Kerbin Orion 4/4 - SYNC", "Kerbin Orion 1/4 - SYNC", "Kerbin Orion 1/3 - 500K",
            "Kerbin Vega 3/4 - SYNC", "Mun Orion 3/4 - 100K", "Kerbin Orion 3/4 - SYNC",
        })!;
        Assert.True(g.FromOurNames);
        Assert.Equal(4, g.Count);
        Assert.Equal("Kerbin Orion", g.Label);
        Assert.Equal(new[] { "Kerbin Orion 1/4 - SYNC", "Kerbin Orion 2/4 - SYNC",
                             "Kerbin Orion 3/4 - SYNC", "Kerbin Orion 4/4 - SYNC" }, g.Members);
    }

    [Fact]
    public void OurNamesKeepTheirSizeEvenWithSatellitesMissing()
    {
        var g = ConstellationGroup.Suggest("Kerbin Orion 2/6 - SYNC", new[] { "Kerbin Orion 5/6 - SYNC" })!;
        Assert.Equal(6, g.Count);
        Assert.Equal(2, g.Members.Count);
    }

    [Fact]
    public void GuessesFreeNamesWithATrailingNumber()
    {
        var g = ConstellationGroup.Suggest("Kerbin Sat 3", new[]
        {
            "Kerbin Sat 1", "Kerbin Sat 2", "Kerbin Sat 4", "Kerbin Sat 5", "Kerbin Sat 6",
            "Kerbin Satellite 7", "Mun Lander",
        })!;
        Assert.False(g.FromOurNames);
        Assert.Equal(6, g.Count);
        Assert.Equal("Kerbin Sat", g.Label);
        Assert.Equal(6, g.Members.Count);
        Assert.Equal("Kerbin Sat 1", g.Members[0]);
    }

    [Fact]
    public void FreeNamesUseTheHighestNumberAsTheSize()
    {
        var g = ConstellationGroup.Suggest("Relay-1", new[] { "Relay-4" })!;
        Assert.Equal(4, g.Count);
    }

    [Fact]
    public void FreeNamesAllowBigConstellations()
    {
        var names = Enumerable.Range(1, 30).Select(k => $"Relay {k}").ToList();
        Assert.Equal(30, ConstellationGroup.Suggest("Relay 1", names)!.Count);
        var many = Enumerable.Range(1, 50).Select(k => $"Relay {k}").ToList();
        Assert.Equal(StationKeeping.MaxSlots, ConstellationGroup.Suggest("Relay 1", many)!.Count);
    }

    [Fact]
    public void FitsThirtySixSlots()
    {
        var lon = Enumerable.Range(0, 36).Select(k => k * 10.0 + 0.3).ToList();
        var fit = StationKeeping.Fit(lon, 36);
        Assert.All(fit.ErrorsDeg, e => Assert.True(Math.Abs(e) < 1e-9));
    }

    [Fact]
    public void NoSuggestionForAnUnnumberedOrLonelyVessel()
    {
        Assert.Null(ConstellationGroup.Suggest("Mun Lander", new[] { "Kerbin Sat 1" }));
        Assert.Null(ConstellationGroup.Suggest("Kerbin Sat 1", new[] { "Mun Lander" }));
    }

    // ── Slot fit ──────────────────────────────────────────────────────────────

    [Fact]
    public void EvenlySpacedSatellitesHaveNoError()
    {
        var fit = StationKeeping.Fit(new[] { 10.0, 130.0, 250.0 }, 3);
        Assert.All(fit.ErrorsDeg, e => Assert.True(Math.Abs(e) < 1e-9));
        Precision.Close(10.0, fit.OffsetDeg, "offset");
    }

    [Fact]
    public void BestFitSharesTheErrorAndKeepsTheOrder()
    {
        // 0, 125, 240: best fit offset ≈ 1.67°, errors −1.67, +3.33, −1.67.
        var fit = StationKeeping.Fit(new[] { 0.0, 125.0, 240.0 }, 3);
        Assert.Equal(new[] { 0, 1, 2 }, fit.SlotOf);
        Assert.True(Math.Abs(fit.ErrorsDeg.Sum()) < 0.01);  // circular mean ≈ arithmetic mean
        Assert.True(fit.ErrorsDeg[1] > 3.0 && fit.ErrorsDeg[1] < 3.5);
    }

    [Fact]
    public void ReferenceSatelliteDefinesTheSlots()
    {
        var fit = StationKeeping.Fit(new[] { 0.0, 125.0, 240.0 }, 3, reference: 0);
        Precision.Close(0.0, fit.ErrorsDeg[0], "reference");
        Precision.Close(5.0, fit.ErrorsDeg[1], "second");
        Precision.Close(0.0, fit.ErrorsDeg[2], "third");
    }

    [Fact]
    public void ExcludedSatelliteDoesNotPullTheSlots()
    {
        // The others are exactly 120° apart; the one to correct is 20° ahead.
        var fit = StationKeeping.Fit(new[] { 0.0, 140.0, 240.0 }, 3, exclude: 1);
        Assert.True(Math.Abs(fit.ErrorsDeg[0]) < 1e-9);
        Assert.True(Math.Abs(fit.ErrorsDeg[2]) < 1e-9);
        Precision.Close(20.0, fit.ErrorsDeg[1], "excluded");
    }

    [Fact]
    public void WorksAcrossZeroDegrees()
    {
        var fit = StationKeeping.Fit(new[] { 350.0, 95.0, 230.0 }, 3, reference: 0);
        Precision.Close(-15.0, fit.ErrorsDeg[1], "second");
        Precision.Close(0.0, fit.ErrorsDeg[2], "third");
    }

    [Fact]
    public void MissingSatellitesLeaveTheirSlotsFree()
    {
        // 6 slots, 60° apart; satellites in slots 0, 1 and 4.
        var fit = StationKeeping.Fit(new[] { 0.0, 61.0, 239.0 }, 6, reference: 0);
        Assert.Equal(new[] { 0, 1, 4 }, fit.SlotOf);
        Precision.Close(1.0, fit.ErrorsDeg[1], "second");
        Precision.Close(-1.0, fit.ErrorsDeg[2], "third");
    }

    [Fact]
    public void BunchedSatellitesStillGetDistinctSlots()
    {
        var fit = StationKeeping.Fit(new[] { 0.0, 5.0, 10.0 }, 3, reference: 0);
        Assert.Equal(3, fit.SlotOf.Distinct().Count());
    }

    [Fact]
    public void MoreSatellitesThanSlotsIsAnError() =>
        Assert.Throws<ArgumentException>(() => StationKeeping.Fit(new[] { 0.0, 90.0, 180.0 }, 2));

    [Theory]
    [InlineData(190.0, -170.0)]
    [InlineData(-190.0, 170.0)]
    [InlineData(180.0, 180.0)]
    [InlineData(720.5, 0.5)]
    public void Wraps(double deg, double expected) => Precision.Close(expected, StationKeeping.Wrap180(deg), "wrap");

    [Fact]
    public void TargetOrbitPrefersSyncThenReferenceThenMean()
    {
        var smas = new[] { 100.0, 110.0, 120.0 };
        Precision.Close(200.0, StationKeeping.TargetSma(smas, 1, 200.0), "sync");
        Precision.Close(110.0, StationKeeping.TargetSma(smas, 1, null), "reference");
        Precision.Close(110.0, StationKeeping.TargetSma(new[] { 100.0, 100.0, 130.0 }, null, null), "mean");
    }

    [Fact]
    public void TargetOrbitIgnoresTheSatelliteBeingCorrected() =>
        // Two satellites at 2200 km, the one to correct far below: the target is theirs.
        Precision.Close(2_200.0, StationKeeping.TargetSma(new[] { 2_200.0, 1_200.0, 2_200.0 }, null, null, exclude: 1),
                        "others' mean");

    [Fact]
    public void TransferOnlyWhenFarFromTheTarget()
    {
        Assert.False(StationKeeping.NeedsTransfer(2_200_000, 2_197_600, 2_199_640));  // 2 km off at 2200 km
        Assert.True(StationKeeping.NeedsTransfer(1_200_000, 1_199_800, 2_200_000));
        Assert.True(StationKeeping.NeedsTransfer(2_400_000, 2_200_000, 2_200_000));  // eccentric
    }

    [Fact]
    public void LongerPeriodDriftsBackwards()
    {
        Assert.True(StationKeeping.DriftDegPerOrbit(1001.0, 1000.0) < 0.0);
        Precision.Close(-0.36, StationKeeping.DriftDegPerOrbit(1001.0, 1000.0), "drift");
    }

    [Fact]
    public void PeriodTolerance()
    {
        Assert.True(StationKeeping.PeriodOk(21_601.0, 21_600.0));
        Assert.False(StationKeeping.PeriodOk(21_610.0, 21_600.0));
    }

    // ── Phasing ───────────────────────────────────────────────────────────────

    [Fact]
    public void AheadMeansASlowerHigherPhasingOrbit()
    {
        double r = R(2_863_334);
        var p = _phasing.Plan(Kerbin.Mu, r, r, r, 10.0, 1);
        Assert.True(p.FirstDv > 0.0);
        Assert.True(p.OtherApsisRadius > r);
        Precision.Close(-p.FirstDv, p.SecondDv, "second burn undoes the first");
        Precision.Close(_mech.OrbitalPeriod(r, Kerbin.Mu) * (1 + 10.0 / 360.0), p.Period, "period");
    }

    [Fact]
    public void BehindMeansAFasterLowerPhasingOrbit()
    {
        double r = R(2_863_334);
        var p = _phasing.Plan(Kerbin.Mu, r, r, r, -10.0, 2);
        Assert.True(p.FirstDv < 0.0);
        Assert.True(p.OtherApsisRadius < r);
        Precision.Close(2 * p.Period, p.Duration, "duration");
    }

    [Fact]
    public void PhasingRemovesTheErrorExactly()
    {
        // Simulate: the satellite does k phasing orbits, its slot moves at the target rate.
        double r = R(1_000_000), mu = Kerbin.Mu, error = 7.5;
        var p = _phasing.Plan(mu, r, r, r, error, 3);
        double tTarget = _mech.OrbitalPeriod(r, mu);
        double slotMoved = 360.0 * p.Duration / tTarget;
        double satMoved  = 360.0 * p.Orbits;
        Precision.Close(error, slotMoved - satMoved, "phase recovered");
    }

    [Fact]
    public void MoreOrbitsCostLess()
    {
        double r = R(2_863_334);
        var one = _phasing.Plan(Kerbin.Mu, r, r, r, 20.0, 1);
        var four = _phasing.Plan(Kerbin.Mu, r, r, r, 20.0, 4);
        Assert.True(four.TotalDv < one.TotalDv);
    }

    [Fact]
    public void SecondBurnSetsTheTargetPeriodFromAnOffOrbit()
    {
        // Slightly too high: the second burn lands on the target semi-major axis.
        double rTarget = R(2_863_334), r = rTarget + 2_000, mu = Kerbin.Mu;
        var p = _phasing.Plan(mu, r, r, rTarget, 5.0, 1);
        double vAfter = _mech.VisViva(r, p.Sma, mu) + p.SecondDv;
        Precision.Close(_mech.VisViva(r, rTarget, mu), vAfter, "speed on the target period");
        Precision.Close(_mech.VisViva(r, p.Sma, mu) - _mech.VisViva(r, r, mu), p.FirstDv, "first burn");
    }

    [Fact]
    public void ChooseTakesMoreOrbitsWhenOnePhasingOrbitIsTooLow()
    {
        // Low orbit, far behind: one orbit would dip into the atmosphere.
        double r = R(150_000), min = PhasingPlanner.MinRadius(Kerbin);
        var one = _phasing.Plan(Kerbin.Mu, r, r, r, -60.0, 1);
        Assert.True(one.OtherApsisRadius < min);
        var p = _phasing.Choose(Kerbin.Mu, r, r, r, -60.0, min, Kerbin.Soi!.Value)!;
        Assert.True(p.Orbits > 1);
        Assert.True(p.OtherApsisRadius >= min);
    }

    [Fact]
    public void ChooseGoesTheLongWayWhenCatchingUpIsNeverSafe()
    {
        // Just above the safe altitude there is no room to drop: lose 350° instead of gaining 10°.
        double min = PhasingPlanner.MinRadius(Kerbin), r = min + 1_000;
        var p = _phasing.Choose(Kerbin.Mu, r, r, r, -10.0, min, Kerbin.Soi!.Value)!;
        Precision.Close(350.0, p.ErrorDeg, "long way");
        Assert.True(p.OtherApsisRadius > r);
    }

    [Fact]
    public void ChooseHonoursMinOrbits()
    {
        double r = R(2_863_334);
        var p = _phasing.Choose(Kerbin.Mu, r, r, r, 10.0, PhasingPlanner.MinRadius(Kerbin), Kerbin.Soi!.Value,
                                minOrbits: 3)!;
        Assert.Equal(3, p.Orbits);
    }

    [Fact]
    public void ChooseReturnsNullWhenNothingFits()
    {
        double r = R(2_863_334);
        Assert.Null(_phasing.Choose(Kerbin.Mu, r, r, r, 90.0, r - 1.0, r + 1.0));
    }

    [Fact]
    public void TrimSetsTheTargetSpeed()
    {
        double rT = R(2_863_334), r = rT + 500, mu = Kerbin.Mu;
        double dv = _phasing.TrimDv(mu, r, r, rT);
        Assert.True(dv < 0.0);
        Precision.Close(_mech.VisViva(r, rT, mu), _mech.VisViva(r, r, mu) + dv, "speed");
    }
}
