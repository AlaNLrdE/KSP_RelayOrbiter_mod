namespace RelayOrbiter.Core.Tests;

public class InterplanetaryTransferTests
{
    private static readonly KspBodyDatabase Db = new();
    private static CelestialBody B(string name) => Db.Find(name)!;
    private readonly InterplanetaryPlanner _planner = new(new OrbitalMechanics());
    private readonly OrbitalMechanics _mech = new();

    private InterplanetaryPlan Plan(string from, string to, double parkingAlt = 100_000, double captureAlt = 100_000)
    {
        var a = B(from); var b = B(to); var parent = B(a.Parent!);
        return _planner.Plan(parent.Mu, a.OrbitalRadius!.Value, b.OrbitalRadius!.Value, a.Mu, a.Radius + parkingAlt,
                             b.Mu, b.Radius + captureAlt);
    }

    [Fact]
    public void KerbinToDunaMatchesTheKnownNumbers()
    {
        // The well-known figures: ~44° phase angle, ~1050 m/s from low Kerbin orbit.
        var p = Plan("Kerbin", "Duna");
        Assert.InRange(p.PhaseAngleDeg, 42.0, 47.0);
        Assert.InRange(p.EjectionDv, 1000.0, 1100.0);
        Assert.True(p.Outward);
        Assert.InRange(p.TransferTime / (6 * 3600 * 426), 0.6, 0.8);  // about 0.7 Kerbin years
    }

    [Fact]
    public void KerbinToEveGoesInwards()
    {
        var p = Plan("Kerbin", "Eve");
        Assert.False(p.Outward);
        Assert.InRange(p.PhaseAngleDeg, 300.0, 320.0);  // Eve ~54° behind Kerbin
        Assert.InRange(p.EjectionDv, 1000.0, 1150.0);
    }

    [Fact]
    public void EjectionIsTheHyperbolicExcessFromTheParkingOrbit()
    {
        var p = Plan("Kerbin", "Duna");
        double r0 = B("Kerbin").Radius + 100_000, mu = B("Kerbin").Mu;
        Precision.Close(Math.Sqrt(p.DepartureVInf * p.DepartureVInf + 2 * mu / r0) - Math.Sqrt(mu / r0),
                        p.EjectionDv, "ejection");
    }

    [Fact]
    public void BothArriveTogether()
    {
        var p = Plan("Kerbin", "Duna");
        double tDuna = _mech.OrbitalPeriod(B("Duna").OrbitalRadius!.Value, B("Kerbol").Mu);
        Precision.Close(180.0, p.PhaseAngleDeg + 360.0 * p.TransferTime / tDuna, "arrival");
    }

    [Fact]
    public void MoonToMoonWorksTheSameWay()
    {
        var p = Plan("Mun", "Minmus", 20_000, 20_000);
        Assert.True(p.Outward);
        Assert.InRange(p.EjectionDv, 200.0, 400.0);
        Assert.True(p.CaptureDv < 0.0);
    }

    // ── Routes ────────────────────────────────────────────────────────────────

    private static string? ParentOf(string name) => Db.Find(name)?.Parent;

    [Theory]
    [InlineData("Kerbin", "Kerbin", TransferRoute.None)]
    [InlineData("Kerbin", "Mun", TransferRoute.Moon)]
    [InlineData("Kerbin", "Duna", TransferRoute.Sibling)]
    [InlineData("Mun", "Minmus", TransferRoute.Sibling)]
    [InlineData("Kerbin", "Ike", TransferRoute.SiblingsMoon)]
    [InlineData("Kerbin", "Laythe", TransferRoute.SiblingsMoon)]
    [InlineData("Mun", "Kerbin", TransferRoute.Unsupported)]
    [InlineData("Mun", "Ike", TransferRoute.Unsupported)]
    public void FindsTheRoute(string from, string to, TransferRoute route) =>
        Assert.Equal(route, Routes.Find(from, to, ParentOf));
}
