namespace RelayOrbiter.Core.Tests;

public class NodeBurnTests
{
    [Fact]
    public void FullThrottleWhenFarFromDoneAndAligned() =>
        Assert.Equal(1.0, NodeBurn.Throttle(100.0, 5.0, 0.0));

    [Fact]
    public void NoThrottleWhenMisaligned() =>
        Assert.Equal(0.0, NodeBurn.Throttle(100.0, 5.0, NodeBurn.MaxAlignmentDeg + 0.1));

    [Fact]
    public void ThrottleEasesOffNearTheEnd()
    {
        double t = NodeBurn.Throttle(1.0, 5.0, 0.0);  // 1 m/s left at 5 m/s²
        Assert.InRange(t, 0.01, 0.5);
    }

    [Fact]
    public void ThrottleNeverBelowMinimumWhileDvLeft() =>
        Assert.True(NodeBurn.Throttle(0.06, 50.0, 0.0) >= 0.01);

    [Fact]
    public void ThrottleDecreasesWithPointingError() =>
        Assert.True(NodeBurn.Throttle(100.0, 5.0, 3.0) < NodeBurn.Throttle(100.0, 5.0, 0.0));

    [Fact]
    public void ThrottleZeroWhenDone() =>
        Assert.Equal(0.0, NodeBurn.Throttle(0.0, 5.0, 0.0));

    [Fact]
    public void BurnCompletesWhenDvLeftIsTiny()
    {
        var b = new NodeBurn();
        Assert.False(b.Update(50.0));
        Assert.False(b.Update(1.0));
        Assert.True(b.Update(NodeBurn.DoneDv / 2));
    }

    [Fact]
    public void BurnStopsOnOvershoot()
    {
        var b = new NodeBurn();
        Assert.False(b.Update(10.0));
        Assert.False(b.Update(0.3));
        Assert.False(b.Update(0.6));  // grew by 0.3: still within tolerance
        Assert.True(b.Update(0.9));   // grew by 0.6 above the minimum: stop
    }

    // ── Tail of the burn ──────────────────────────────────────────────────────

    [Fact]
    public void NotInTailWhileMuchDvLeft() =>
        Assert.False(NodeBurn.IsTail(100.0, 5.0));  // 20 s of thrust left

    [Fact]
    public void InTailWhenLessThanTailSecondsOfThrustLeft() =>
        Assert.True(NodeBurn.IsTail(5.0 * NodeBurn.TailHoldSeconds * 0.9, 5.0));

    [Fact]
    public void TailBoundaryIsTailHoldSecondsOfFullThrust() =>
        Assert.False(NodeBurn.IsTail(5.0 * NodeBurn.TailHoldSeconds, 5.0));

    [Fact]
    public void AlwaysInTailWithoutThrust() =>
        Assert.True(NodeBurn.IsTail(100.0, 0.0));

    [Fact]
    public void NegativeProjectedDvMeansOvershootAndCompletes()
    {
        var b = new NodeBurn();
        Assert.False(b.Update(3.0));
        Assert.True(b.Update(-0.2));  // remaining Δv now points backwards along the burn
    }
}
