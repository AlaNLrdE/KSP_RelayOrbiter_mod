namespace RelayOrbiter.Core.Tests;

public class AscentStagerTests
{
    [Fact]
    public void StagesAfterAMomentWithoutThrustWhenThrustIsWanted()
    {
        var s = new AscentStager();
        Assert.False(s.ShouldStage(0.0, 0.0, 1.0));
        Assert.False(s.ShouldStage(0.3, 0.0, 1.0));
        Assert.True(s.ShouldStage(0.6, 0.0, 1.0));
    }

    [Fact]
    public void NeverStagesWhileAnEngineBurns() =>
        Assert.False(new AscentStager().ShouldStage(10.0, 200.0, 1.0));

    [Fact]
    public void NeverStagesDuringACoast()
    {
        var s = new AscentStager();
        s.ShouldStage(0.0, 0.0, 0.0);
        Assert.False(s.ShouldStage(5.0, 0.0, 0.0));
    }

    [Fact]
    public void WaitsAgainAfterStaging()
    {
        var s = new AscentStager();
        s.ShouldStage(0.0, 0.0, 1.0);
        Assert.True(s.ShouldStage(1.0, 0.0, 1.0));
        s.Staged(1.0);
        Assert.False(s.ShouldStage(1.2, 0.0, 1.0));  // the new stage may still be igniting
        Assert.True(s.ShouldStage(1.6, 0.0, 1.0));   // a stage without engines (fairings): the next one
        Assert.Equal(1, s.StagedSinceThrust);
    }

    [Fact]
    public void ThrustResetsTheCount()
    {
        var s = new AscentStager();
        s.Staged(0.0); s.Staged(1.0);
        Assert.Equal(2, s.StagedSinceThrust);
        s.ShouldStage(2.0, 100.0, 1.0);
        Assert.Equal(0, s.StagedSinceThrust);
    }

    [Fact]
    public void ARefusedStageDoesNotCount()
    {
        var s = new AscentStager();
        s.Staged(0.0, fired: false);
        Assert.Equal(0, s.StagedSinceThrust);
    }

    [Fact]
    public void TheUsersCraftStopsAboveTheSatellites()
    {
        // Sat array extra++: carrier engine in stage 2, satellites' engines in 1 and 0.
        var satellites = new HashSet<uint> { 101, 102 };
        Assert.Equal(StagingDecision.Stage, StagingGuard.Decide(2, new uint[] { 7, 8 }, satellites, 1));
        Assert.Equal(StagingDecision.WouldReleaseSatellite, StagingGuard.Decide(1, new uint[] { 9, 101 }, satellites, 0));
    }

    [Fact]
    public void ADecouplerThatWouldDropTheSatellitesIsRefused()
    {
        // The user's EnginePlate2 in stage 2: none of its own parts are a satellite's,
        // but firing it cuts loose the satellites hanging on its far side.
        var satellites = new HashSet<uint> { 101, 102 };
        Assert.Equal(StagingDecision.WouldReleaseSatellite,
                     StagingGuard.Decide(2, new uint[] { 7, 8 }, satellites, 0, releasedParts: new uint[] { 50, 101 }));
        Assert.Equal(StagingDecision.Stage,
                     StagingGuard.Decide(2, new uint[] { 7, 8 }, satellites, 0, releasedParts: new uint[] { 50, 51 }));
    }
}
