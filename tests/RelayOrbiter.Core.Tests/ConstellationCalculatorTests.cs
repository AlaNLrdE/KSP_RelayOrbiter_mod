namespace RelayOrbiter.Core.Tests;

public class ConstellationCalculatorTests
{
    private readonly ConstellationCalculator _calc = new(new OrbitalMechanics());

    [Fact]
    public void NullMechanicsThrows() =>
        Assert.Throws<ArgumentNullException>(() => new ConstellationCalculator(null!));

    [Theory]
    [MemberData(nameof(GoldenVectors.Cases), MemberType = typeof(GoldenVectors))]
    public void ResultMatchesGoldenVectors(string name, int index)
    {
        var g = GoldenVectors.Body(name);
        var e = g.Results[index];
        var a = _calc.Calculate(g.ToCelestialBody(), e.HTarget);

        Assert.Equal(g.Name, a.BodyName);
        Precision.Close(e.HTarget,     a.HTarget,     "h_target");
        Precision.Close(e.RC,          a.RC,          "r_c");
        Precision.Close(e.TC,          a.TC,          "T_c");
        Precision.Close(e.VC,          a.VC,          "v_c");
        Precision.Close(e.TE,          a.TE,          "T_e");
        Precision.Close(e.AE,          a.AE,          "a_e");
        Precision.Close(e.RApo,        a.RApo,        "r_apo");
        Precision.Close(e.HApo,        a.HApo,        "h_apo");
        Precision.Close(e.RPeri,       a.RPeri,       "r_peri");
        Precision.Close(e.HPeri,       a.HPeri,       "h_peri");
        Precision.Close(e.VApo,        a.VApo,        "v_apo");
        Precision.Close(e.VPeri,       a.VPeri,       "v_peri");
        Assert.Equal(e.SurfaceOk,      a.SurfaceOk);
        Assert.Equal(e.AtmOk,          a.AtmOk);
        Precision.Close(e.MinHSurface, a.MinHSurface, "min_h_surface");
        Precision.Close(e.MinHAtm,     a.MinHAtm,     "min_h_atm");
    }
}
