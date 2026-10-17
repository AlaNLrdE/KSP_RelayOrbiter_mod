namespace RelayOrbiter.Core.Tests;

public class DeploymentPlannerTests
{
    private readonly DeploymentPlanner _planner = new(new OrbitalMechanics());

    [Fact]
    public void NullMechanicsThrows() =>
        Assert.Throws<ArgumentNullException>(() => new DeploymentPlanner(null!));

    // ── Plan from a circular orbit, checked against the C++ golden vectors ──────

    [Theory]
    [MemberData(nameof(GoldenVectors.Cases), MemberType = typeof(GoldenVectors))]
    public void PlanFromCircularOrbitMatchesGoldenVectors(string name, int index)
    {
        var g = GoldenVectors.Body(name);
        var e = g.Results[index];
        const double burnUT = 1_000_000.0;

        // A circular orbit at the target altitude: apoapsis radius = semi-major axis = r_c.
        var p = _planner.Plan(g.Mu, e.RC, e.RC, burnUT);

        Precision.Close(burnUT,         p.BurnUT,          "burn UT");
        Precision.Close(e.RApo,         p.ReleaseRadius,   "release radius");
        Precision.Close(e.VApo - e.VC,  p.ParkingDeltaV,   "parking Δv");
        Precision.Close(e.RPeri,        p.PeriapsisRadius, "periapsis radius");
        Precision.Close(e.TC,           p.CircularPeriod,  "circular period");
        Precision.Close(e.TE,           p.ParkingPeriod,   "parking period");
        Precision.Close(e.VC - e.VApo,  p.SatelliteCircularizeDeltaV, "satellite circularize Δv");
    }

    [Fact]
    public void ParkingBurnIsRetrograde()
    {
        var kerbin = new KspBodyDatabase().Find("Kerbin")!;
        double r = kerbin.Radius + 2_863_334.0;
        Assert.True(_planner.Plan(kerbin.Mu, r, r, 0.0).ParkingDeltaV < 0.0);
    }

    [Fact]
    public void ReleasesAreOneParkingOrbitApart()
    {
        var kerbin = new KspBodyDatabase().Find("Kerbin")!;
        double r = kerbin.Radius + 2_863_334.0;
        var p = _planner.Plan(kerbin.Mu, r, r, 500.0);

        Assert.Equal(3, p.ReleaseUTs.Count);
        Precision.Close(500.0,                       p.ReleaseUTs[0], "SAT-1 release");
        Precision.Close(500.0 + p.ParkingPeriod,     p.ReleaseUTs[1], "SAT-2 release");
        Precision.Close(500.0 + 2 * p.ParkingPeriod, p.ReleaseUTs[2], "SAT-3 release");
    }

    [Fact]
    public void PlanFromEllipticalOrbitUsesApoapsisSpeed()
    {
        // Burn at the apoapsis of a non-circular orbit: Δv is relative to the
        // actual apoapsis speed, and the constellation forms at the apoapsis radius.
        var mech = new OrbitalMechanics();
        var kerbin = new KspBodyDatabase().Find("Kerbin")!;
        double rApo = kerbin.Radius + 1_000_000.0;
        double sma  = kerbin.Radius + 800_000.0;
        var p = _planner.Plan(kerbin.Mu, rApo, sma, 0.0);

        double aE = rApo * Math.Pow(2.0 / 3.0, 2.0 / 3.0);
        Precision.Close(rApo, p.ReleaseRadius, "release radius");
        Precision.Close(mech.VisViva(rApo, aE, kerbin.Mu) - mech.VisViva(rApo, sma, kerbin.Mu),
                        p.ParkingDeltaV, "parking Δv");
        Precision.Close(p.CircularPeriod * 2.0 / 3.0, p.ParkingPeriod, "parking period");
    }

    // ── Circularize ───────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(GoldenVectors.Cases), MemberType = typeof(GoldenVectors))]
    public void CircularizingParkingOrbitMatchesGoldenVectors(string name, int index)
    {
        var g = GoldenVectors.Body(name);
        var e = g.Results[index];
        Precision.Close(e.VC - e.VApo, _planner.CircularizeAt(g.Mu, e.RApo, e.AE), "circularize Δv");
    }

    [Fact]
    public void CircularOrbitNeedsNoCircularization()
    {
        var kerbin = new KspBodyDatabase().Find("Kerbin")!;
        double r = kerbin.Radius + 100_000.0;
        Precision.Close(0.0, _planner.CircularizeAt(kerbin.Mu, r, r), "circularize Δv");
    }

    // ── Next apoapsis ─────────────────────────────────────────────────────────

    [Fact]
    public void NextApoapsisWhenFarEnoughAway() =>
        Assert.Equal(1_300.0, _planner.NextPassUT(1_000.0, 300.0, 5_000.0, 60.0));

    [Fact]
    public void NextApoapsisSkipsOnePeriodWhenTooClose() =>
        Assert.Equal(6_010.0, _planner.NextPassUT(1_000.0, 10.0, 5_000.0, 60.0));

    [Fact]
    public void NextApoapsisExactlyAtLeadIsKept() =>
        Assert.Equal(1_060.0, _planner.NextPassUT(1_000.0, 60.0, 5_000.0, 60.0));
}
