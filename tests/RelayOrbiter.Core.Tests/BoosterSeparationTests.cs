namespace RelayOrbiter.Core.Tests;

public class BoosterSeparationTests
{
    // Sat array Super: core cluster (1) and two SRBs (2, 3) in stage 8; stage 7 holds the
    // radial decouplers (4, 5) and the SRB nose cones' separation motors (6, 7), and drops
    // decoupler, SRB and nose cone on each side.
    private static readonly uint[] Stage7 = { 4, 5, 6, 7 };
    private static readonly uint[] DroppedBy7 = { 2, 3, 4, 5, 6, 7 };

    private static Dictionary<uint, EngineState> Engines(EngineState core, EngineState srbs) => new()
    {
        [1] = core, [2] = srbs, [3] = srbs, [6] = EngineState.Unlit, [7] = EngineState.Unlit,
        [9] = EngineState.Unlit,  // the upper stage's engine, staying on board
    };

    [Fact]
    public void DropsBurntOutSrbsWhileTheCoreBurns() =>
        Assert.True(BoosterSeparation.DropsOnlySpentBoosters(
            Stage7, DroppedBy7, Engines(EngineState.Running, EngineState.Spent)));

    [Fact]
    public void KeepsSrbsThatStillBurn() =>
        Assert.False(BoosterSeparation.DropsOnlySpentBoosters(
            Stage7, DroppedBy7, Engines(EngineState.Running, EngineState.Running)));

    [Fact]
    public void NeverDropsTheRunningCore() =>
        // Stage 6's Decoupler.3 drops the burning core cluster with its tanks.
        Assert.False(BoosterSeparation.DropsOnlySpentBoosters(
            new uint[] { 10 }, new uint[] { 1, 10, 11 }, Engines(EngineState.Running, EngineState.Spent)));

    [Fact]
    public void NeverLightsAnEngineThatStaysOnBoard() =>
        // A stage that drops the SRBs but also ignites the upper stage: a hot staging.
        Assert.False(BoosterSeparation.DropsOnlySpentBoosters(
            new uint[] { 4, 5, 6, 7, 9 }, DroppedBy7, Engines(EngineState.Running, EngineState.Spent)));

    [Fact]
    public void NeverDropsAnUnlitEngine() =>
        Assert.False(BoosterSeparation.DropsOnlySpentBoosters(
            new uint[] { 4 }, new uint[] { 2, 4, 9 }, Engines(EngineState.Running, EngineState.Spent)));

    [Fact]
    public void AStageWithoutSpentEnginesIsNotABoosterDrop() =>
        // A fairing, say: nothing burnt out to drop.
        Assert.False(BoosterSeparation.DropsOnlySpentBoosters(
            new uint[] { 20 }, new uint[] { 20, 21 }, Engines(EngineState.Running, EngineState.Spent)));
}
