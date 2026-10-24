namespace RelayOrbiter.Core.Tests;

public class AscentDeltaVTests
{
    private static readonly KspBodyDatabase Db = new();

    [Fact]
    public void LowKerbinOrbitIsAboutTheKnownFigure() =>
        Assert.InRange(LaunchPlanner.AscentDeltaV(Db.Find("Kerbin")!, 100_000, 0.0), 3300.0, 3500.0);

    [Fact]
    public void RetrogradeCostsTheRotationTwice()
    {
        var kerbin = Db.Find("Kerbin")!;
        double surface = 2 * Math.PI * kerbin.Radius / kerbin.RotPeriod;
        double prograde = LaunchPlanner.AscentDeltaV(kerbin, 100_000, 0.0);
        double at135 = LaunchPlanner.AscentDeltaV(kerbin, 100_000, 135.0);
        Precision.Close(surface * (1 - Math.Cos(135 * Math.PI / 180)), at135 - prograde, "rotation");
        Assert.InRange(at135 - prograde, 280.0, 320.0);
    }

    [Fact]
    public void AirlessBodiesNeedMuchLess() =>
        Assert.True(LaunchPlanner.AscentDeltaV(Db.Find("Mun")!, 20_000, 0.0) < 900.0);
}
