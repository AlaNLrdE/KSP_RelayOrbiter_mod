namespace RelayOrbiter.Core.Tests;

public class MoonTransferTests
{
    private static readonly KspBodyDatabase Db = new();
    private static readonly CelestialBody Kerbin = Db.Find("Kerbin")!;
    private static readonly CelestialBody Mun = Db.Find("Mun")!;
    private static readonly CelestialBody Minmus = Db.Find("Minmus")!;
    private readonly MoonTransferPlanner _planner = new(new OrbitalMechanics());
    private readonly OrbitalMechanics _mech = new();

    private MoonTransferPlan ToMun(double altitude = 100_000, double munPeriapsis = 100_000) =>
        _planner.Plan(Kerbin.Mu, Kerbin.Radius + altitude, Mun.OrbitalRadius!.Value, Mun.Mu, Mun.Radius + munPeriapsis);

    [Fact]
    public void LowKerbinOrbitToTheMunMatchesTheKnownNumbers()
    {
        // The well-known figures: ~110° phase angle, ~840-860 m/s ejection.
        var p = ToMun();
        Assert.InRange(p.PhaseAngleDeg, 108.0, 113.0);
        Assert.InRange(p.EjectionDv, 830.0, 870.0);
        Assert.InRange(p.CaptureDv, -320.0, -230.0);  // circular 100 km around the Mun
    }

    [Fact]
    public void PhaseAngleMakesBothArriveTogether()
    {
        var p = ToMun();
        double moonPeriod = _mech.OrbitalPeriod(Mun.OrbitalRadius!.Value, Kerbin.Mu);
        double moonMoved = 360.0 * p.TransferTime / moonPeriod;
        Precision.Close(180.0, p.PhaseAngleDeg + moonMoved, "the moon reaches the arrival point");
    }

    [Fact]
    public void EjectionIsTheHohmannFirstBurn()
    {
        double r1 = Kerbin.Radius + 100_000, r2 = Mun.OrbitalRadius!.Value, mu = Kerbin.Mu;
        var p = ToMun();
        Precision.Close(Math.Sqrt(mu / r1) * (Math.Sqrt(2 * r2 / (r1 + r2)) - 1), p.EjectionDv, "ejection");
        Precision.Close((r1 + r2) / 2, p.TransferSma, "transfer sma");
    }

    [Fact]
    public void CaptureFromVInfinity()
    {
        var p = ToMun(munPeriapsis: 50_000);
        double rp = Mun.Radius + 50_000;
        double vp = Math.Sqrt(p.ArrivalVInf * p.ArrivalVInf + 2 * Mun.Mu / rp);
        Precision.Close(Math.Sqrt(Mun.Mu / rp) - vp, p.CaptureDv, "capture");
        Precision.Close(Math.Abs(p.EjectionDv) + Math.Abs(p.CaptureDv), p.TotalDv, "total");
    }

    [Fact]
    public void MinmusIsCheaperToCaptureAt()
    {
        var p = _planner.Plan(Kerbin.Mu, Kerbin.Radius + 100_000, Minmus.OrbitalRadius!.Value, Minmus.Mu,
                              Minmus.Radius + 50_000);
        Assert.InRange(p.EjectionDv, 900.0, 960.0);
        Assert.True(Math.Abs(p.CaptureDv) < Math.Abs(ToMun().CaptureDv));
    }

    [Fact]
    public void FromAboveTheMoonTheBurnIsRetrograde()
    {
        var p = _planner.Plan(Kerbin.Mu, 20_000_000, Mun.OrbitalRadius!.Value, Mun.Mu, Mun.Radius + 100_000);
        Assert.True(p.EjectionDv < 0.0);
    }

    // ── Window ────────────────────────────────────────────────────────────────

    [Fact]
    public void PhaseAngleIsTheMoonAheadOfTheVessel()
    {
        Precision.Close(110.0, MoonTransferPlanner.PhaseAngleDeg(10.0, 120.0), "ahead");
        Precision.Close(350.0, MoonTransferPlanner.PhaseAngleDeg(20.0, 10.0), "just behind");
    }

    [Fact]
    public void InnerVesselWaitsForTheAngleToShrink()
    {
        // Vessel 1000 s, moon 10000 s: gains 0.324°/s. From 120° to 110°: 10° / 0.324.
        double wait = MoonTransferPlanner.WaitTime(120.0, 110.0, 1000.0, 10000.0);
        Precision.Close(10.0 / (0.36 - 0.036), wait, "wait");
    }

    [Fact]
    public void JustMissedTheWindowWaitsAlmostASynodicPeriod()
    {
        double wait = MoonTransferPlanner.WaitTime(109.0, 110.0, 1000.0, 10000.0);
        double synodic = MoonTransferPlanner.SynodicPeriod(1000.0, 10000.0);
        Assert.True(wait > 0.99 * synodic && wait < synodic);
    }

    [Fact]
    public void OuterVesselWaitsForTheAngleToGrow()
    {
        double wait = MoonTransferPlanner.WaitTime(100.0, 110.0, 20000.0, 10000.0);
        Precision.Close(10.0 / (0.036 - 0.018), wait, "wait");
    }

    [Fact]
    public void SameOrbitNeverHasAWindow() =>
        Assert.True(double.IsPositiveInfinity(MoonTransferPlanner.WaitTime(10.0, 20.0, 5000.0, 5000.0)));
}
