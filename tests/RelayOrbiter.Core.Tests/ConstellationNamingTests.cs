namespace RelayOrbiter.Core.Tests;

public class ConstellationNamingTests
{
    [Fact]
    public void FirstCallsignWhenNothingExists() =>
        Assert.Equal("Aurora", ConstellationNaming.Callsign("Kerbin", new[] { "Sat array", "Mun Lander" }));

    [Fact]
    public void SkipsCallsignsUsedAroundTheSameBody() =>
        Assert.Equal("Comet", ConstellationNaming.Callsign("Kerbin",
            new[] { "Kerbin Aurora 1/3 - SYNC", "Kerbin Beacon 2/4 - 1000K" }));

    [Fact]
    public void CallsignsAreIndependentPerBody() =>
        Assert.Equal("Aurora", ConstellationNaming.Callsign("Mun", new[] { "Kerbin Aurora 1/3 - SYNC" }));

    [Fact]
    public void CallsignPrefixIsMatchedAsAWord() =>
        // "Kerbin Aurorae ..." does not use "Aurora".
        Assert.Equal("Aurora", ConstellationNaming.Callsign("Kerbin", new[] { "Kerbin Aurorae 1/3 - SYNC" }));

    [Fact]
    public void AfterAllCallsignsComesTheSecondSeries()
    {
        var used = ConstellationNaming.Callsigns.Select(c => $"Kerbin {c} 1/3 - SYNC");
        Assert.Equal("Aurora II", ConstellationNaming.Callsign("Kerbin", used));
    }

    [Fact]
    public void ThirdSeriesWhenTheSecondIsUsedToo()
    {
        var used = ConstellationNaming.Callsigns.Select(c => $"Kerbin {c} 1/3 - SYNC")
            .Concat(ConstellationNaming.Callsigns.Select(c => $"Kerbin {c} II 1/3 - SYNC"));
        Assert.Equal("Aurora III", ConstellationNaming.Callsign("Kerbin", used));
    }

    [Theory]
    [InlineData(2_863_334.0, 2_863_334.0, "SYNC")]
    [InlineData(2_880_000.0, 2_863_334.0, "SYNC")]   // within 1%
    [InlineData(1_000_000.0, 2_863_334.0, "1000K")]
    [InlineData(1_234_567.0, null, "1235K")]
    public void OrbitTags(double altitude, double? sync, string expected) =>
        Assert.Equal(expected, ConstellationNaming.OrbitTag(altitude, sync));

    [Fact]
    public void EquatorialSatelliteName() =>
        Assert.Equal("Kerbin Orion 2/6 - SYNC",
                     ConstellationNaming.SatelliteName("Kerbin", "Orion", 2, 6, "SYNC", 0.08));

    [Fact]
    public void InclinedSatelliteName() =>
        Assert.Equal("Kerbin Orion 2/6 - SYNC i10",
                     ConstellationNaming.SatelliteName("Kerbin", "Orion", 2, 6, "SYNC", 10.08));
}
