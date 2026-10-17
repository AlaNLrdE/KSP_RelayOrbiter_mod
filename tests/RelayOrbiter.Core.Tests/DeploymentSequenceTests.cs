namespace RelayOrbiter.Core.Tests;

public class DeploymentSequenceTests
{
    private static IReadOnlyList<SequenceStep> Steps() =>
        DeploymentSequence.Build(carrierBurn: 40.0, satelliteBurns: new[] { 90.0, 100.0 });

    [Fact]
    public void StepOrder()
    {
        var kinds = Steps().Select(s => (s.Kind, s.Satellite)).ToArray();
        var expected = new[]
        {
            (StepKind.WarpToApoapsis, -1),
            (StepKind.OrientForRelease, 0),
            (StepKind.Release, 0),
            (StepKind.Wait, -1),
            (StepKind.CreateParkingNode, -1),
            (StepKind.ExecuteNode, -1),

            (StepKind.WarpToApoapsis, -1),
            (StepKind.OrientForRelease, 1),
            (StepKind.Release, 1),
            (StepKind.Wait, -1),
            (StepKind.SwitchToSatellite, 1),
            (StepKind.CreateCircularizeNode, 1),
            (StepKind.ExecuteNode, 1),
            (StepKind.SwitchToCarrier, -1),

            (StepKind.WarpToApoapsis, -1),
            (StepKind.OrientForRelease, 2),
            (StepKind.Release, 2),
            (StepKind.Wait, -1),
            (StepKind.SwitchToSatellite, 2),
            (StepKind.CreateCircularizeNode, 2),
            (StepKind.ExecuteNode, 2),
            (StepKind.SwitchToCarrier, -1),
        };
        Assert.Equal(expected, kinds);
    }

    [Fact]
    public void FirstReleaseLeavesTimeForTheParkingBurn()
    {
        var warp = Steps()[0];
        double expected = 40.0 / 2 + DeploymentSequence.AlignLead + DeploymentSequence.SeparationWait
                        + DeploymentSequence.ReleaseMargin + DeploymentSequence.OrientTime;
        Assert.Equal(expected, warp.Seconds);
    }

    [Fact]
    public void LaterReleasesLeaveTimeForSwitchAlignAndHalfBurn()
    {
        var warps = Steps().Where(s => s.Kind == StepKind.WarpToApoapsis).ToList();
        double fixedLead = DeploymentSequence.AlignLead + DeploymentSequence.SeparationWait
                         + DeploymentSequence.SwitchMargin + DeploymentSequence.OrientTime;
        Assert.Equal(90.0 / 2 + fixedLead,  warps[1].Seconds);
        Assert.Equal(100.0 / 2 + fixedLead, warps[2].Seconds);
    }

    [Fact]
    public void WaitsAreTheSeparationWait() =>
        Assert.All(Steps().Where(s => s.Kind == StepKind.Wait),
                   s => Assert.Equal(DeploymentSequence.SeparationWait, s.Seconds));

    [Fact]
    public void RequiresTwoSatelliteBurns() =>
        Assert.Throws<ArgumentException>(() => DeploymentSequence.Build(40.0, new[] { 90.0 }));

    [Fact]
    public void OrientationTimeoutFitsInTheLead() =>
        Assert.True(SeparationAttitude.TimeoutSeconds <= DeploymentSequence.OrientTime);
}
