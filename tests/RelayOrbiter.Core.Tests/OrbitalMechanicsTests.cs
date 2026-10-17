namespace RelayOrbiter.Core.Tests;

public class OrbitalMechanicsTests
{
    private readonly OrbitalMechanics _mech = new();

    [Theory]
    [MemberData(nameof(GoldenVectors.BodyNames), MemberType = typeof(GoldenVectors))]
    public void SyncOrbitAltitudeMatchesGoldenVectors(string name)
    {
        var g = GoldenVectors.Body(name);
        Precision.Close(g.SyncAltitude, _mech.SyncOrbitAltitude(g.ToCelestialBody()), "sync_altitude");
    }

    [Theory]
    [MemberData(nameof(GoldenVectors.BodyNames), MemberType = typeof(GoldenVectors))]
    public void SoiMaxAltitudeMatchesGoldenVectors(string name)
    {
        var g = GoldenVectors.Body(name);
        Precision.Close(g.SoiMaxAltitude, _mech.SoiMaxAltitude(g.ToCelestialBody()), "soi_max_altitude");
    }

    [Theory]
    [MemberData(nameof(GoldenVectors.Cases), MemberType = typeof(GoldenVectors))]
    public void OrbitalPeriodMatchesGoldenVectors(string name, int index)
    {
        var g = GoldenVectors.Body(name);
        var r = g.Results[index];
        Precision.Close(r.TC, _mech.OrbitalPeriod(r.RC, g.Mu), "T_c");
    }

    [Theory]
    [MemberData(nameof(GoldenVectors.Cases), MemberType = typeof(GoldenVectors))]
    public void VisVivaMatchesGoldenVectors(string name, int index)
    {
        var g = GoldenVectors.Body(name);
        var r = g.Results[index];
        Precision.Close(r.VC,    _mech.VisViva(r.RC,    r.RC, g.Mu), "v_c");
        Precision.Close(r.VApo,  _mech.VisViva(r.RApo,  r.AE, g.Mu), "v_apo");
        Precision.Close(r.VPeri, _mech.VisViva(r.RPeri, r.AE, g.Mu), "v_peri");
    }
}
