namespace RelayOrbiter.Core.Tests;

public class StagingGuardTests
{
    private static readonly ISet<uint> Satellites = new HashSet<uint> { 20, 21, 22 };

    [Fact]
    public void StagesWhenTheNextStageHasNoSatelliteParts() =>
        Assert.Equal(StagingDecision.Stage, StagingGuard.Decide(3, new uint[] { 5, 6 }, Satellites, 0));

    [Fact]
    public void AnEmptyStageIsSafe() =>
        Assert.Equal(StagingDecision.Stage, StagingGuard.Decide(3, Array.Empty<uint>(), Satellites, 0));

    [Fact]
    public void NeverReleasesASatelliteStillOnBoard() =>
        Assert.Equal(StagingDecision.WouldReleaseSatellite,
                     StagingGuard.Decide(2, new uint[] { 5, 21 }, Satellites, 0));

    [Fact]
    public void NothingLeftBelowStageZero() =>
        Assert.Equal(StagingDecision.NothingLeft, StagingGuard.Decide(-1, Array.Empty<uint>(), Satellites, 0));

    [Fact]
    public void StopsAfterTooManyStagesInOneBurn() =>
        Assert.Equal(StagingDecision.TooManyStages,
                     StagingGuard.Decide(3, new uint[] { 5 }, Satellites, StagingGuard.MaxStagesPerBurn));

    [Fact]
    public void ReleasedSatellitesNoLongerBlock() =>
        Assert.Equal(StagingDecision.Stage,
                     StagingGuard.Decide(2, new uint[] { 30 }, new HashSet<uint>(), 0));
}
