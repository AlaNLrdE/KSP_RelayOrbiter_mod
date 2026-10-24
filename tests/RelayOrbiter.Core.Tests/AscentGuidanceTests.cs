namespace RelayOrbiter.Core.Tests;

public class AscentGuidanceTests
{
    private static readonly CelestialBody Kerbin = new KspBodyDatabase().Find("Kerbin")!;
    private const double OrbitalSpeed = 2246.0, SurfaceSpeed = 174.9;

    [Fact]
    public void StraightUpOffThePad()
    {
        var c = new AscentGuidance(100_000, 70_000).Update(200, 20, 300, 10);
        Assert.Equal(AscentPhase.Vertical, c.Phase);
        Assert.Equal(90.0, c.PitchDeg);
        Assert.Equal(1.0, c.Throttle);
    }

    [Fact]
    public void PitchesOverWithAltitude()
    {
        double end = AscentGuidance.TurnEndAltitude(100_000, 70_000);
        Precision.Close(59_500, end, "turn end");
        Assert.Equal(90.0, AscentGuidance.PitchDeg(500, end));
        double p10 = AscentGuidance.PitchDeg(10_000, end), p30 = AscentGuidance.PitchDeg(30_000, end);
        Assert.True(p10 < 90 && p30 < p10 && p30 > 0);
        Assert.Equal(0.0, AscentGuidance.PitchDeg(65_000, end));
    }

    [Fact]
    public void BurnsUntilTheApoapsisReachesTheTarget()
    {
        var g = new AscentGuidance(100_000, 70_000);
        var burning = g.Update(30_000, 900, 60_000, 60);
        Assert.Equal(AscentPhase.GravityTurn, burning.Phase);
        Assert.Equal(1.0, burning.Throttle);
        var near = g.Update(40_000, 1500, 98_000, 80);
        Assert.True(near.Throttle < 1.0 && near.Throttle > 0.0);
        var coast = g.Update(50_000, 1800, 100_500, 90);
        Assert.Equal(AscentPhase.Coast, coast.Phase);
        Assert.Equal(0.0, coast.Throttle);
    }

    [Fact]
    public void TopsUpGentlyAboveTheTurn()
    {
        // Drag took the apoapsis a little below the target after the turn: a light burn.
        var c = new AscentGuidance(100_000, 70_000).Update(65_000, 2000, 99_500, 60);
        Assert.Equal(AscentPhase.GravityTurn, c.Phase);
        Assert.True(c.Throttle <= 0.2);
    }

    [Fact]
    public void CircularizationIsPlannedAsSoonAsOutOfTheAtmosphere()
    {
        var g = new AscentGuidance(100_000, 70_000);
        Assert.Equal(AscentPhase.Coast, g.Update(65_000, 2000, 100_200, 120).Phase);       // still in the air
        Assert.Equal(AscentPhase.Circularize, g.Update(70_500, 2000, 100_200, 200).Phase); // just out: plan it
    }

    [Fact]
    public void OutOfTheAtmosphereButShortTopsUpFirst() =>
        Assert.Equal(AscentPhase.GravityTurn, new AscentGuidance(100_000, 70_000).Update(72_000, 2000, 99_000, 150).Phase);

    [Fact]
    public void AirlessBodiesTurnEarly() =>
        Assert.Equal(10_000.0, AscentGuidance.TurnEndAltitude(30_000, 0.0));

    [Fact]
    public void EquatorialLaunchGoesEast() =>
        Precision.Close(90.0, AscentGuidance.LaunchHeadingDeg(0.0, 0.0, OrbitalSpeed, SurfaceSpeed), "east");

    [Fact]
    public void PolarLaunchLeansWestToCancelTheRotation()
    {
        double h = AscentGuidance.LaunchHeadingDeg(90.0, 0.0, OrbitalSpeed, SurfaceSpeed);
        Assert.InRange(h, 350.0, 359.9);  // a little west of north
    }

    [Fact]
    public void RetrogradeInclinationHeadsNorthWest()
    {
        double h = AscentGuidance.LaunchHeadingDeg(135.0, 0.0, OrbitalSpeed, SurfaceSpeed);
        Assert.InRange(h, 300.0, 315.0);  // inertial 315°, more westward against the rotation
    }

    [Fact]
    public void UnreachableInclinationGoesDueEast() =>
        Assert.InRange(AscentGuidance.LaunchHeadingDeg(0.0, 28.0, OrbitalSpeed, SurfaceSpeed), 89.0, 91.0);

    [Fact]
    public void AngleOfAttackIsTightWhileTheAirPushes()
    {
        Assert.Equal(AscentGuidance.MaxAoADeg, AscentGuidance.AllowedAoADeg(20.0));
        Assert.Equal(180.0, AscentGuidance.AllowedAoADeg(0.2));
    }

    [Fact]
    public void ThrottlesDownAboveMaxPressure()
    {
        Assert.Equal(1.0, AscentGuidance.LimitThrottle(1.0, 25.0));
        Precision.Close(0.5, AscentGuidance.LimitThrottle(1.0, 60.0), "half");
        Assert.Equal(0.1, AscentGuidance.LimitThrottle(1.0, 1000.0));
    }
}
