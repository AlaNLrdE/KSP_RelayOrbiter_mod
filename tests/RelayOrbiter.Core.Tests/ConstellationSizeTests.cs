namespace RelayOrbiter.Core.Tests;

// N satellites and the inner / outer parking orbit.
public class ConstellationSizeTests
{
    private readonly ConstellationCalculator _calc = new(new OrbitalMechanics());
    private readonly DeploymentPlanner _planner = new(new OrbitalMechanics());
    private static readonly CelestialBody Kerbin = new KspBodyDatabase().Find("Kerbin")!;
    private static readonly CelestialBody Mun    = new KspBodyDatabase().Find("Mun")!;

    // ── Resonance ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(6)] [InlineData(12)]
    public void InnerPeriodRatioIsNMinusOneOverN(int n) =>
        Assert.Equal((n - 1.0) / n, Resonance.PeriodRatio(n, ParkingOrbit.Inner), 12);

    [Theory]
    [InlineData(2)] [InlineData(3)] [InlineData(6)]
    public void OuterPeriodRatioIsNPlusOneOverN(int n) =>
        Assert.Equal((n + 1.0) / n, Resonance.PeriodRatio(n, ParkingOrbit.Outer), 12);

    [Theory]
    [InlineData(3, 120.0)] [InlineData(6, 60.0)] [InlineData(4, 90.0)] [InlineData(2, 180.0)]
    public void SpacingIs360OverN(int n, double deg) =>
        Assert.Equal(deg, Resonance.SpacingDeg(n), 12);

    [Theory]
    [InlineData(1)] [InlineData(0)] [InlineData(13)]
    public void SatelliteCountOutOfRangeThrows(int n) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Resonance.PeriodRatio(n, ParkingOrbit.Inner));

    // ── ConstellationCalculator ───────────────────────────────────────────────

    [Fact]
    public void DefaultsAreThreeSatellitesOnTheInnerOrbit()
    {
        var a = _calc.Calculate(Kerbin, 2_863_334.0);
        var b = _calc.Calculate(Kerbin, 2_863_334.0, 3, ParkingOrbit.Inner);
        Assert.Equal(3, a.Satellites);
        Assert.Equal(ParkingOrbit.Inner, a.Parking);
        Assert.Equal(b.TE, a.TE);
        Assert.Equal(b.RPeri, a.RPeri);
    }

    [Theory]
    [InlineData(2)] [InlineData(6)] [InlineData(12)]
    public void InnerOrbitForNSatellites(int n)
    {
        var r = _calc.Calculate(Kerbin, 2_863_334.0, n, ParkingOrbit.Inner);
        Assert.Equal(n, r.Satellites);
        Assert.Equal(360.0 / n, r.SpacingDeg, 12);
        Precision.Close((n - 1.0) / n, r.TE / r.TC, "T_e / T_c");
        Precision.Close(r.RC, r.RApo, "apoapsis on the target orbit");
        Precision.Close(2.0 * r.AE - r.RC, r.RPeri, "periapsis");
        Assert.True(r.CarrierDv < 0.0);      // retrograde at apoapsis
        Assert.True(r.SatelliteDv > 0.0);    // satellites circularize prograde
        Precision.Close(-r.CarrierDv, r.SatelliteDv, "symmetric burns from a circular orbit");
    }

    [Theory]
    [InlineData(2)] [InlineData(3)] [InlineData(6)]
    public void OuterOrbitForNSatellites(int n)
    {
        var r = _calc.Calculate(Kerbin, 500_000.0, n, ParkingOrbit.Outer);
        Assert.Equal(ParkingOrbit.Outer, r.Parking);
        Precision.Close((n + 1.0) / n, r.TE / r.TC, "T_e / T_c");
        Precision.Close(r.RC, r.RPeri, "periapsis on the target orbit");
        Precision.Close(2.0 * r.AE - r.RC, r.RApo, "apoapsis");
        Assert.True(r.RApo > r.RC);
        Assert.True(r.CarrierDv > 0.0);      // prograde at periapsis
        Assert.True(r.SatelliteDv < 0.0);    // satellites circularize retrograde
        Assert.True(r.SurfaceOk && r.AtmOk);
        Assert.True(r.SoiOk);
    }

    [Fact]
    public void OuterApoapsisBeyondSoiIsFlagged()
    {
        // Close to the Mun's SOI edge, the outer parking orbit leaves the SOI.
        var r = _calc.Calculate(Mun, 1_900_000.0, 3, ParkingOrbit.Outer);
        Assert.False(r.SoiOk);
    }

    [Fact]
    public void InnerOrbitIsAlwaysInsideTheSoi() =>
        Assert.True(_calc.Calculate(Mun, 1_900_000.0, 3, ParkingOrbit.Inner).SoiOk);

    [Theory]
    [InlineData(2_863_334.0, 3, ParkingOrbit.Inner)]  // synchronous orbit: inner periapsis is safe
    [InlineData(100_000.0, 3, ParkingOrbit.Outer)]    // low orbit: inner periapsis underground
    [InlineData(1_000_000.0, 2, ParkingOrbit.Outer)]  // N = 2 at 1000 km: inner periapsis underground
    [InlineData(2_863_334.0, 2, ParkingOrbit.Inner)]  // N = 2 at synchronous: inner still fits
    [InlineData(100_000.0, 6, ParkingOrbit.Outer)]
    public void RecommendsInnerWhenSafeOtherwiseOuter(double h, int n, ParkingOrbit expected) =>
        Assert.Equal(expected, _calc.Recommend(Kerbin, h, n));

    // ── DeploymentPlanner ─────────────────────────────────────────────────────

    [Fact]
    public void SixReleasesFiveSixthsOfAnOrbitApart()
    {
        double r = Kerbin.Radius + 2_863_334.0;
        var p = _planner.Plan(Kerbin.Mu, r, r, 100.0, 6, ParkingOrbit.Inner);
        Assert.Equal(6, p.Satellites);
        Assert.Equal(6, p.ReleaseUTs.Count);
        Precision.Close(p.CircularPeriod * 5.0 / 6.0, p.ParkingPeriod, "parking period");
        for (int k = 0; k < 6; ++k)
            Precision.Close(100.0 + k * p.ParkingPeriod, p.ReleaseUTs[k], $"SAT-{k + 1} release");
    }

    [Fact]
    public void OuterPlanBurnsProgradeAndReleasesAtPeriapsis()
    {
        double r = Kerbin.Radius + 500_000.0;
        var p = _planner.Plan(Kerbin.Mu, r, r, 0.0, 3, ParkingOrbit.Outer);
        Assert.Equal(ParkingOrbit.Outer, p.Parking);
        Assert.True(p.ParkingDeltaV > 0.0);
        Assert.True(p.SatelliteCircularizeDeltaV < 0.0);
        Precision.Close(r, p.PeriapsisRadius, "periapsis = release radius");
        Assert.True(p.ApoapsisRadius > r);
        Precision.Close(p.CircularPeriod * 4.0 / 3.0, p.ParkingPeriod, "parking period");
    }

    [Fact]
    public void InnerPlanApoapsisIsTheReleaseRadius()
    {
        double r = Kerbin.Radius + 2_863_334.0;
        Precision.Close(r, _planner.Plan(Kerbin.Mu, r, r, 0.0).ApoapsisRadius, "apoapsis");
    }

    [Fact]
    public void CircularizingAtPeriapsisIsRetrograde()
    {
        double r = Kerbin.Radius + 500_000.0;
        var p = _planner.Plan(Kerbin.Mu, r, r, 0.0, 3, ParkingOrbit.Outer);
        double aE = (p.PeriapsisRadius + p.ApoapsisRadius) / 2.0;
        Precision.Close(p.SatelliteCircularizeDeltaV, _planner.CircularizeAt(Kerbin.Mu, r, aE), "Δv");
    }

    // ── DeploymentSequence ────────────────────────────────────────────────────

    [Fact]
    public void SequenceForSixSatellites()
    {
        var steps = DeploymentSequence.Build(40.0, new[] { 9.0, 9.0, 9.0, 9.0, 9.0 });
        Assert.Equal(6 + 5 * 8, steps.Count);
        Assert.Equal(Enumerable.Range(0, 6), steps.Where(s => s.Kind == StepKind.Release).Select(s => s.Satellite));
        Assert.Equal(5, steps.Count(s => s.Kind == StepKind.SwitchToSatellite));
    }

    [Fact]
    public void SequenceForTwoSatellites()
    {
        var steps = DeploymentSequence.Build(40.0, new[] { 9.0 });
        Assert.Equal(6 + 8, steps.Count);
    }

    [Fact]
    public void SequenceNeedsAtLeastOneSatelliteBurn() =>
        Assert.Throws<ArgumentException>(() => DeploymentSequence.Build(40.0, Array.Empty<double>()));

    // ── PartTree ──────────────────────────────────────────────────────────────

    // Carrier core(1) with engine(2); four satellites on decouplers 10, 20, 30, 40.
    private static PartTree FourSatellites()
    {
        var parts = new List<PartInfo>
        {
            new(1, null, "Carrier core", isCommand: true, mass: 1.0),
            new(2, 1, "Carrier engine", mass: 1.0, thrust: 60.0, isp: 345.0, engineActive: true),
        };
        foreach (uint d in new uint[] { 10, 20, 30, 40 })
        {
            parts.Add(new(d, 1, $"Decoupler {d}", SeparatorKind.Decoupler, decouplesFromParent: true, mass: 0.05));
            parts.Add(new(d + 1, d, $"Probe {d}", isCommand: true, mass: 0.1));
            parts.Add(new(d + 2, d + 1, $"Engine {d}", mass: 0.2, thrust: 2.0, isp: 300.0));
        }
        return new PartTree(parts);
    }

    [Fact]
    public void PlanReleasesNeedsExactlyTheConfiguredCount()
    {
        var t = FourSatellites();
        var all = t.Candidates();
        Assert.True(t.PlanReleases(all, satellites: 4).IsValid);
        Assert.True(t.PlanReleases(all.Take(2).ToList(), satellites: 2).IsValid);
        var p = t.PlanReleases(all.Take(3).ToList(), satellites: 4);
        Assert.False(p.IsValid);
        Assert.Contains("4", p.Error);
    }
}
