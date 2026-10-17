namespace RelayOrbiter.Core.Tests;

public class BurnTimingTests
{
    [Fact]
    public void MatchesRocketEquation()
    {
        // 10 t, 60 kN, Isp 345 s, 1000 m/s
        double ve = 345.0 * BurnTiming.G0;
        double expected = 10.0 * ve / 60.0 * (1.0 - Math.Exp(-1000.0 / ve));
        Precision.Close(expected, BurnTiming.Duration(1000.0, 10.0, 60.0, 345.0)!.Value, "duration");
    }

    [Fact]
    public void ShortBurnApproachesMassTimesDvOverThrust() =>
        Assert.Equal(10.0 * 1.0 / 60.0, BurnTiming.Duration(1.0, 10.0, 60.0, 345.0)!.Value, 1e-4);

    [Fact]
    public void NegativeDvUsesMagnitude() =>
        Assert.Equal(BurnTiming.Duration(171.0, 2.0, 2.0, 300.0), BurnTiming.Duration(-171.0, 2.0, 2.0, 300.0));

    [Fact]
    public void ZeroDvTakesNoTime() =>
        Assert.Equal(0.0, BurnTiming.Duration(0.0, 2.0, 2.0, 300.0));

    [Theory]
    [InlineData(0.0, 300.0)]
    [InlineData(-1.0, 300.0)]
    [InlineData(2.0, 0.0)]
    public void NoThrustOrIspGivesNull(double thrust, double isp) =>
        Assert.Null(BurnTiming.Duration(100.0, 2.0, thrust, isp));

    // ── Rocket equation Δv ───────────────────────────────────────────────────

    [Fact]
    public void DeltaVFromTheRocketEquation() =>
        Precision.Close(345.0 * BurnTiming.G0 * Math.Log(10.0 / 6.0), BurnTiming.DeltaV(345.0, 10.0, 6.0), "Δv");

    [Fact]
    public void NoFuelNoDeltaV() =>
        Assert.Equal(0.0, BurnTiming.DeltaV(345.0, 10.0, 10.0));

    [Theory]
    [InlineData(0.0, 10.0, 6.0)]
    [InlineData(345.0, 0.0, 0.0)]
    [InlineData(345.0, 6.0, 10.0)]  // dry mass above wet mass
    public void InvalidInputsGiveZero(double isp, double wet, double dry) =>
        Assert.Equal(0.0, BurnTiming.DeltaV(isp, wet, dry));
}
