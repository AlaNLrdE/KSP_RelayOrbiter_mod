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
        double start = AscentGuidance.TurnStartAltitude(70_000), end = AscentGuidance.TurnEndAltitude(100_000, 70_000);
        Precision.Close(59_500, end, "turn end");
        Assert.Equal(90.0, AscentGuidance.PitchDeg(500, start, end));
        double p10 = AscentGuidance.PitchDeg(10_000, start, end), p30 = AscentGuidance.PitchDeg(30_000, start, end);
        Assert.True(p10 < 90 && p30 < p10 && p30 > 0);
        Assert.Equal(0.0, AscentGuidance.PitchDeg(65_000, start, end));
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
        Assert.Equal(AscentGuidance.MaxAoADeg, AscentGuidance.AllowedAoADeg(10.0));
        Assert.Equal(180.0, AscentGuidance.AllowedAoADeg(0.0));
    }

    [Fact]
    public void AngleOfAttackShrinksAsTheDynamicPressureGrows()
    {
        // q × α held: the Duna launch's 43 kPa allows ~1.4°, not 5°.
        Precision.Close(60.0 / 43.0, AscentGuidance.AllowedAoADeg(43.0), "43 kPa");
        Precision.Close(2.0, AscentGuidance.AllowedAoADeg(30.0), "30 kPa");
        Assert.Equal(AscentGuidance.MinAoADeg, AscentGuidance.AllowedAoADeg(200.0));
        // The Kerbin launches that flew fine (2° at 27 kPa) stay within it.
        Assert.True(AscentGuidance.AllowedAoADeg(27.0) >= 2.0);
    }

    [Fact]
    public void TheCommandTurnsSlowlyInThickAir()
    {
        // The Duna launch's nose swung at 7.7°/s: the command now turns at most 2°/s there.
        Assert.Equal(AscentGuidance.MaxSlewInAirDegPerS, AscentGuidance.MaxSlewDegPerS(43.0));
        Assert.True(AscentGuidance.MaxSlewDegPerS(0.5) > AscentGuidance.MaxSlewInAirDegPerS);
        Assert.Equal(AscentGuidance.MaxSlewFreeDegPerS, AscentGuidance.MaxSlewDegPerS(0.0));
        Assert.Equal(AscentGuidance.MaxSlewFreeDegPerS, AscentGuidance.MaxSlewDegPerS(0.01));
    }

    [Fact]
    public void TheDunaLaunchsTumbleIsDetectedEarlier()
    {
        // 21:45:49.9: 20° at 43 kPa. With ~1.4° allowed the abort threshold is ~16.4°.
        Assert.True(AscentGuidance.IsDeparting(20.0, 43.0));
        Assert.False(AscentGuidance.IsDeparting(0.7, 36.0));
    }

    [Fact]
    public void AngleOfAttackLimitFadesOutWithTheAir()
    {
        // No jump from 5° to free (the old 1 kPa switch): it widens as q drops.
        Assert.Equal(AscentGuidance.MaxAoADeg, AscentGuidance.AllowedAoADeg(AscentGuidance.AoAFadeoutPressure));
        Precision.Close(12.5, AscentGuidance.AllowedAoADeg(1.0), "1 kPa");
        Precision.Close(25.0, AscentGuidance.AllowedAoADeg(0.5), "0.5 kPa");
        Assert.Equal(180.0, AscentGuidance.AllowedAoADeg(0.01));
        double previous = AscentGuidance.MaxAoADeg;
        for (double q = 2.4; q > 0.05; q -= 0.1)
        {
            double allowed = AscentGuidance.AllowedAoADeg(q);
            Assert.True(allowed >= previous, $"q {q:F1}");
            previous = allowed;
        }
    }

    [Fact]
    public void TheTurnStartsAboveTheThickAir()
    {
        Assert.Equal(3_500.0, AscentGuidance.TurnStartAltitude(70_000));   // Kerbin
        Assert.Equal(2_500.0, AscentGuidance.TurnStartAltitude(50_000));   // Duna
        Assert.Equal(AscentGuidance.MinTurnStartAltitude, AscentGuidance.TurnStartAltitude(0.0));
    }

    [Fact]
    public void TheMunLaunchHeldVerticalThroughItsMaxQ()
    {
        // Sat array Super (SRBs, 17 MN) was at 2.2 km, 77° and at the 5° limit at q 23 kPa:
        // the program now still asks for vertical there.
        var c = new AscentGuidance(250_000, 70_000).Update(2_200, 220, 5_000, 30);
        Assert.Equal(90.0, c.PitchDeg);
    }

    [Fact]
    public void TheMunLaunchsTumbleIsDetected()
    {
        // Logged at 20:40:52 (38.5° at 3.3 kPa) and 20:41:12 (50.9° at 19.5 kPa).
        Assert.True(AscentGuidance.IsDeparting(38.5, 3.3));
        Assert.True(AscentGuidance.IsDeparting(50.9, 19.5));
        // ...but not its controlled climb (7° at 23.3 kPa), nor a free nose in thin air.
        Assert.False(AscentGuidance.IsDeparting(7.0, 23.3));
        Assert.False(AscentGuidance.IsDeparting(21.5, 0.5));
        Assert.False(AscentGuidance.IsDeparting(98.0, 0.0));  // on the pad
    }

    [Fact]
    public void ThrottlesDownAboveMaxPressure()
    {
        Assert.Equal(1.0, AscentGuidance.LimitThrottle(1.0, 25.0));
        Precision.Close(0.5, AscentGuidance.LimitThrottle(1.0, 60.0), "half");
        Assert.Equal(0.1, AscentGuidance.LimitThrottle(1.0, 1000.0));
    }
}
