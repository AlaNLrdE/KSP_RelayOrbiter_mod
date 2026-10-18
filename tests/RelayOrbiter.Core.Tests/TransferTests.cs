namespace RelayOrbiter.Core.Tests;

public class TransferTests
{
    private readonly TransferPlanner _planner = new(new OrbitalMechanics());
    private readonly OrbitalMechanics _mech = new();
    private static readonly CelestialBody Kerbin = new KspBodyDatabase().Find("Kerbin")!;
    private static double R(double altitude) => Kerbin.Radius + altitude;

    // ── Hohmann transfer ──────────────────────────────────────────────────────

    [Fact]
    public void RaisingFromLowOrbitBurnsProgradeAtPeriapsis()
    {
        var t = _planner.Plan(Kerbin.Mu, R(100_000), R(100_000), R(2_863_334));
        Assert.True(t.Needed);
        Assert.Equal(Apsis.Periapsis, t.FirstBurnAt);
        Assert.True(t.FirstDv > 0.0);
        Assert.True(t.SecondDv > 0.0);
    }

    [Fact]
    public void HohmannDeltaVsMatchTheTextbookFormula()
    {
        double r1 = R(100_000), r2 = R(2_863_334), mu = Kerbin.Mu;
        var t = _planner.Plan(mu, r1, r1, r2);
        double dv1 = Math.Sqrt(mu / r1) * (Math.Sqrt(2 * r2 / (r1 + r2)) - 1);
        double dv2 = Math.Sqrt(mu / r2) * (1 - Math.Sqrt(2 * r1 / (r1 + r2)));
        Precision.Close(dv1, t.FirstDv, "Δv1");
        Precision.Close(dv2, t.SecondDv, "Δv2");
        Precision.Close((r1 + r2) / 2, t.TransferSma, "transfer sma");
        Precision.Close(dv1 + dv2, t.TotalDv, "total");
    }

    [Fact]
    public void LoweringBurnsRetrogradeAtApoapsis()
    {
        var t = _planner.Plan(Kerbin.Mu, R(2_863_334), R(2_863_334), R(500_000));
        Assert.Equal(Apsis.Apoapsis, t.FirstBurnAt);
        Assert.True(t.FirstDv < 0.0);
        Assert.True(t.SecondDv < 0.0);
        Precision.Close(R(2_863_334), t.FirstBurnRadius, "burn radius");
    }

    [Fact]
    public void FromEllipticalOrbitUsesTheActualSpeedAtTheBurnApsis()
    {
        // Ap 1000 km, Pe 200 km, target 2000 km: burn at periapsis.
        double ap = R(1_000_000), pe = R(200_000), rT = R(2_000_000), mu = Kerbin.Mu;
        var t = _planner.Plan(mu, ap, pe, rT);
        double expected = _mech.VisViva(pe, (pe + rT) / 2, mu) - _mech.VisViva(pe, (ap + pe) / 2, mu);
        Assert.Equal(Apsis.Periapsis, t.FirstBurnAt);
        Precision.Close(expected, t.FirstDv, "Δv1");
    }

    [Fact]
    public void LoweringApoapsisOnlyStillBurnsAtPeriapsis()
    {
        // Target between Pe and Ap: lower the apoapsis from the periapsis.
        var t = _planner.Plan(Kerbin.Mu, R(3_000_000), R(500_000), R(1_000_000));
        Assert.Equal(Apsis.Periapsis, t.FirstBurnAt);
        Assert.True(t.FirstDv < 0.0);
    }

    [Theory]
    [InlineData(2_863_334.0, 2_863_334.0)]
    [InlineData(2_863_800.0, 2_862_900.0)]  // within 1 km
    public void AlreadyAtTheTargetNeedsNoTransfer(double ap, double pe) =>
        Assert.False(_planner.Plan(Kerbin.Mu, R(ap), R(pe), R(2_863_334)).Needed);

    [Fact]
    public void FarFromTheTargetNeedsATransfer() =>
        Assert.True(_planner.Plan(Kerbin.Mu, R(2_870_000), R(2_860_000), R(2_863_334)).Needed);

    // ── Plane change ──────────────────────────────────────────────────────────

    [Fact]
    public void IncreasingInclinationAtAscendingNodePushesNormal()
    {
        var (prograde, normal) = PlaneChange.DeltaV(1000.0, 0.0, 10.0, ascending: true);
        Precision.Close(1000.0 * Math.Sin(10 * Math.PI / 180), normal, "normal");
        Precision.Close(1000.0 * (Math.Cos(10 * Math.PI / 180) - 1), prograde, "prograde");
    }

    [Fact]
    public void AtDescendingNodeTheNormalComponentFlips()
    {
        var an = PlaneChange.DeltaV(1000.0, 0.0, 10.0, ascending: true);
        var dn = PlaneChange.DeltaV(1000.0, 0.0, 10.0, ascending: false);
        Precision.Close(-an.normal, dn.normal, "normal");
        Precision.Close(an.prograde, dn.prograde, "prograde");
    }

    [Fact]
    public void DecreasingInclinationPushesAntiNormalAtAscendingNode() =>
        Assert.True(PlaneChange.DeltaV(1000.0, 30.0, 0.0, ascending: true).normal < 0.0);

    [Fact]
    public void PlaneChangeMagnitudeIsTwoVSinHalfAngle()
    {
        var (p, n) = PlaneChange.DeltaV(1000.0, 5.0, 50.0, ascending: true);
        Precision.Close(2 * 1000.0 * Math.Sin(45.0 / 2 * Math.PI / 180), Math.Sqrt(p * p + n * n), "|Δv|");
    }

    [Theory]
    [InlineData(0.0, true, 0.0)]
    [InlineData(90.0, true, 270.0)]
    [InlineData(90.0, false, 90.0)]
    [InlineData(300.0, true, 60.0)]
    [InlineData(300.0, false, 240.0)]
    public void NodeTrueAnomalies(double argPe, bool ascending, double expected) =>
        Assert.Equal(expected, PlaneChange.NodeTrueAnomalyDeg(argPe, ascending), 9);

    [Theory]
    [InlineData(0.0, 0.05, false)]
    [InlineData(0.0, 0.2, true)]
    [InlineData(45.0, 45.0, false)]
    [InlineData(90.0, 0.0, true)]
    public void PlaneChangeNeededBeyondTolerance(double current, double target, bool needed) =>
        Assert.Equal(needed, PlaneChange.Needed(current, target));

    // ── Sequence prelude ──────────────────────────────────────────────────────

    [Fact]
    public void NoPreludeByDefault() =>
        Assert.Equal(StepKind.WarpToRelease, DeploymentSequence.Build(40.0, new[] { 9.0, 9.0 })[0].Kind);

    [Fact]
    public void TransferThenPlaneChangeWhenRaising()
    {
        var steps = DeploymentSequence.Build(40.0, new[] { 9.0, 9.0 },
            new TransferSteps(transfer: true, planeChange: true, planeChangeFirst: false));
        var kinds = steps.Take(6).Select(s => s.Kind);
        Assert.Equal(new[]
        {
            StepKind.CreateTransferNode, StepKind.ExecuteNode,
            StepKind.CreateTargetCircularizeNode, StepKind.ExecuteNode,
            StepKind.CreatePlaneChangeNode, StepKind.ExecuteNode,
        }, kinds);
        Assert.Equal(StepKind.WarpToRelease, steps[6].Kind);
    }

    [Fact]
    public void PlaneChangeFirstWhenLowering()
    {
        var steps = DeploymentSequence.Build(40.0, new[] { 9.0, 9.0 },
            new TransferSteps(transfer: true, planeChange: true, planeChangeFirst: true));
        Assert.Equal(StepKind.CreatePlaneChangeNode, steps[0].Kind);
        Assert.Equal(StepKind.CreateTransferNode, steps[2].Kind);
    }

    [Fact]
    public void OnlyPlaneChange()
    {
        var steps = DeploymentSequence.Build(40.0, new[] { 9.0, 9.0 },
            new TransferSteps(transfer: false, planeChange: true, planeChangeFirst: false));
        Assert.Equal(new[] { StepKind.CreatePlaneChangeNode, StepKind.ExecuteNode, StepKind.WarpToRelease },
                     steps.Take(3).Select(s => s.Kind));
    }

    [Fact]
    public void MoonTransferComesFirstThenTheTargetOrbit()
    {
        var steps = DeploymentSequence.Build(40.0, new[] { 9.0, 9.0 },
            new TransferSteps(transfer: true, planeChange: true, planeChangeFirst: false, moon: true));
        Assert.Equal(new[]
        {
            StepKind.CreateMoonPlaneNode, StepKind.ExecuteNode,
            StepKind.CreateEjectionNode, StepKind.ExecuteNode,
            StepKind.CreateCourseCorrectionNode, StepKind.ExecuteNode,
            StepKind.WarpToSoi,
            StepKind.CreateCaptureNode, StepKind.ExecuteNode,
            StepKind.CreateTransferNode,
        }, steps.Take(10).Select(s => s.Kind));
    }

    [Fact]
    public void InterplanetaryLegCorrectsTwice()
    {
        var steps = DeploymentSequence.Build(40.0, new[] { 9.0 },
            new TransferSteps(transfer: true, planeChange: false, planeChangeFirst: false, interplanetary: true));
        Assert.Equal(new[]
        {
            StepKind.CreateEjectionNode, StepKind.ExecuteNode,
            StepKind.CreateCourseCorrectionNode, StepKind.ExecuteNode,
            StepKind.WarpToMidcourse,
            StepKind.CreateCourseCorrectionNode, StepKind.ExecuteNode,
            StepKind.WarpToSoi,
            StepKind.CreateCaptureNode, StepKind.ExecuteNode,
            StepKind.CreateTransferNode,
        }, steps.Take(11).Select(s => s.Kind));
        Assert.All(steps.Take(10), s => Assert.Equal(0, s.Leg));
    }

    [Fact]
    public void MoonOfAnotherPlanetIsTwoLegs()
    {
        var t = new TransferSteps(transfer: true, planeChange: false, planeChangeFirst: false, moon: true, interplanetary: true);
        Assert.Equal(2, t.Legs);
        var steps = DeploymentSequence.Build(40.0, new[] { 9.0 }, t);
        int moonPlane = steps.ToList().FindIndex(s => s.Kind == StepKind.CreateMoonPlaneNode);
        Assert.True(moonPlane > steps.ToList().FindIndex(s => s.Kind == StepKind.CreateCaptureNode));
        Assert.Equal(1, steps[moonPlane].Leg);
        Assert.Equal(0, steps[0].Leg);
    }
}
