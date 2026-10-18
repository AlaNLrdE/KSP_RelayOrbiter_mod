namespace RelayOrbiter.Core.Tests;

public class LaunchPlannerTests
{
    private static readonly KspBodyDatabase Db = new();
    private static CelestialBody Body(string name) => Db.Find(name)!;

    [Theory]
    [InlineData("Kerbin", 100_000.0)]  // 70 km atmosphere + 30 km
    [InlineData("Eve", 120_000.0)]
    [InlineData("Duna", 80_000.0)]
    [InlineData("Mun", 20_000.0)]      // airless and small: the minimum
    [InlineData("Minmus", 20_000.0)]
    [InlineData("Tylo", 30_000.0)]     // airless and big: 5% of 600 km
    public void ParkingAltitudes(string body, double expected) =>
        Assert.Equal(expected, LaunchPlanner.ParkingAltitude(Body(body)), 6);

    [Fact]
    public void LaunchesToTheParkingOrbitBelowAHighTarget() =>
        Assert.Equal(100_000.0, LaunchPlanner.LaunchAltitude(Body("Kerbin"), 2_863_334.0), 6);

    [Fact]
    public void LaunchesStraightToALowTarget() =>
        Assert.Equal(80_000.0, LaunchPlanner.LaunchAltitude(Body("Kerbin"), 80_000.0), 6);

    // ── PartTree before launch ────────────────────────────────────────────────

    private static PartTree OnThePad() => new(new List<PartInfo>
    {
        new(1, null, "Carrier core", isCommand: true, mass: 1.0),
        new(2, 1, "Carrier engine", mass: 1.0, thrust: 60.0, isp: 345.0, engineActive: false),
        new(10, 1, "Dec A", SeparatorKind.Decoupler, decouplesFromParent: true, mass: 0.05),
        new(11, 10, "Probe A", isCommand: true, mass: 0.1),
        new(20, 1, "Dec B", SeparatorKind.Decoupler, decouplesFromParent: true, mass: 0.05),
        new(21, 20, "Probe B", isCommand: true, mass: 0.1),
        new(22, 21, "Engine B", mass: 0.2, thrust: 2.0, isp: 300.0),
    });

    [Fact]
    public void InactiveCarrierEngineBlocksByDefault()
    {
        var t = OnThePad();
        Assert.False(t.PlanReleases(t.Candidates(), satellites: 2).IsValid);
    }

    [Fact]
    public void BeforeLaunchTheCarrierEngineMayBeInactive()
    {
        var t = OnThePad();
        var p = t.PlanReleases(t.Candidates(), satellites: 2, requireActiveCarrierEngine: false);
        Assert.True(p.IsValid, p.Error);
        Assert.Equal(60.0, p.CarrierThrust);  // estimated from the carrier's engines
    }
}
