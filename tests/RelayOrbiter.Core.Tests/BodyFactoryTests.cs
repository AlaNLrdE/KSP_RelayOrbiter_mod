namespace RelayOrbiter.Core.Tests;

public class BodyFactoryTests
{
    private readonly KspBodyDatabase _db = new();

    [Fact]
    public void KerbinFromGameValuesMatchesDatabase()
    {
        var expected = _db.Find("Kerbin")!;
        var b = BodyFactory.FromGame("Kerbin", 600_000.0, 3.5316e12, true, 70_000.0,
                                     21_549.425, 84_159_286.0, 13_599_840_256.0, "Kerbol");

        Assert.Equal(expected.Name,          b.Name);
        Assert.Equal(expected.Radius,        b.Radius);
        Assert.Equal(expected.Mu,            b.Mu);
        Assert.Equal(expected.Atm,           b.Atm);
        Assert.Equal(expected.RotPeriod,     b.RotPeriod);
        Assert.Equal(expected.Soi,           b.Soi);
        Assert.Equal(expected.OrbitalRadius, b.OrbitalRadius);
        Assert.Equal(expected.Parent,        b.Parent);
    }

    [Fact]
    public void StarWithInfiniteSoiHasNoSoiOrbitOrParent()
    {
        var b = BodyFactory.FromGame("Kerbol", 261_600_000.0, 1.1723328e18, false, 0.0,
                                     432_000.0, double.PositiveInfinity, null, null);

        Assert.Equal("Kerbol", b.Name);
        Assert.Equal(261_600_000.0, b.Radius);
        Assert.Null(b.Soi);
        Assert.Null(b.OrbitalRadius);
        Assert.Null(b.Parent);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void InvalidSoiBecomesNull(double soi)
    {
        var b = BodyFactory.FromGame("X", 100_000.0, 1e10, false, 0.0, 10_000.0, soi, 1e7, "Kerbol");
        Assert.Null(b.Soi);
    }

    [Fact]
    public void AtmosphereDepthIgnoredWhenBodyHasNoAtmosphere()
    {
        // KSP can leave a non-zero atmosphereDepth on airless bodies.
        var b = BodyFactory.FromGame("Mun", 200_000.0, 6.5138398e10, false, 1_000.0,
                                     138_984.38, 2_429_559.0, 12_000_000.0, "Kerbin");
        Assert.Equal(0.0, b.Atm);
    }

    [Fact]
    public void AtmosphereDepthKeptWhenBodyHasAtmosphere()
    {
        var b = BodyFactory.FromGame("Duna", 320_000.0, 3.0136321e11, true, 50_000.0,
                                     65_517.859, 47_921_949.0, 20_726_155_264.0, "Kerbol");
        Assert.Equal(50_000.0, b.Atm);
    }
}
