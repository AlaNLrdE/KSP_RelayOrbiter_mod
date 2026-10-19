namespace RelayOrbiter.Core.Tests;

public class PartTreeTests
{
    // Carrier: core(1, root) - tank(2) - engine(3, active).
    // SAT-A on a stack decoupler facing the carrier:        dec(10) - probe(11) - engine(12)
    // SAT-B on a docking-port pair (Jr ports):              port(20) - port(21) - probe(22) - engine(23)
    // SAT-C on a decoupler that drops its child:            dec(30) -> probe(31) - engine(32)
    // Empty decoupler with nothing to command (not a satellite): dec(40) - tank(41)
    private static List<PartInfo> Vessel() => new()
    {
        new(1,  null, "Carrier core", isCommand: true, mass: 0.5),
        new(2,  1,    "Tank", mass: 4.0),
        new(3,  2,    "Carrier engine", mass: 1.0, thrust: 60.0, isp: 345.0, engineActive: true),

        new(10, 2,  "Decoupler A", SeparatorKind.Decoupler, decouplesFromParent: true, mass: 0.05),
        new(11, 10, "Probe A", isCommand: true, mass: 0.1),
        new(12, 11, "Engine A", mass: 0.2, thrust: 2.0, isp: 300.0),

        new(20, 2,  "Port 1", SeparatorKind.DockingPort, mass: 0.02),
        new(21, 20, "Port 2", SeparatorKind.DockingPort, mass: 0.02),
        new(22, 21, "Probe B", isCommand: true, mass: 0.1),
        new(23, 22, "Engine B", mass: 0.2, thrust: 2.0, isp: 300.0),

        new(30, 2,  "Decoupler C", SeparatorKind.Decoupler, decoupledChildId: 31, mass: 0.05),
        new(31, 30, "Probe C", isCommand: true, mass: 0.1),
        new(32, 31, "Engine C", mass: 0.2, thrust: 4.0, isp: 250.0),

        new(40, 2,  "Empty decoupler", SeparatorKind.Decoupler, decouplesFromParent: true, mass: 0.05),
        new(41, 40, "Small tank", mass: 0.3),
    };

    private static SatelliteCandidate Sat(PartTree t, uint separator) =>
        t.Candidates().Single(c => c.SeparatorId == separator);

    [Fact]
    public void FindsDecouplerAndDockingPortSatellites()
    {
        var ids = new PartTree(Vessel()).Candidates().Select(c => c.SeparatorId).OrderBy(i => i);
        Assert.Equal(new uint[] { 10, 21, 30 }, ids);
    }

    [Fact]
    public void DecouplerFacingParentTakesItselfAlong() =>
        Assert.Equal(new uint[] { 10, 11, 12 }, Sat(new PartTree(Vessel()), 10).PartIds.OrderBy(i => i));

    [Fact]
    public void DecouplerDroppingChildStaysOnCarrier() =>
        Assert.Equal(new uint[] { 31, 32 }, Sat(new PartTree(Vessel()), 30).PartIds.OrderBy(i => i));

    [Fact]
    public void DockingPortPairReleasesTheOuterPort()
    {
        var c = Sat(new PartTree(Vessel()), 21);
        Assert.Equal(new uint[] { 21, 22, 23 }, c.PartIds.OrderBy(i => i));
        Assert.Equal("Probe B", c.Name);
        Assert.Equal("Port 2", c.SeparatorTitle);
    }

    [Fact]
    public void SeparatorWithoutCommandPartIsNotASatellite() =>
        Assert.DoesNotContain(new PartTree(Vessel()).Candidates(), c => c.SeparatorId == 40);

    [Fact]
    public void ValidPlanHasStatsPerSatellite()
    {
        var t = new PartTree(Vessel());
        var p = t.PlanReleases(new[] { Sat(t, 10), Sat(t, 21), Sat(t, 30) });

        Assert.True(p.IsValid, p.Error);
        Assert.Equal(3, p.Satellites.Count);
        Assert.Equal(0.35, p.Satellites[0].Mass, 1e-9);
        Assert.Equal(0.32, p.Satellites[1].Mass, 1e-9);  // inner port 20 stays on the carrier
        Assert.Equal(0.30, p.Satellites[2].Mass, 1e-9);
        Assert.Equal(4.0,  p.Satellites[2].Thrust);
        Assert.Equal(250.0, p.Satellites[2].Isp);
    }

    [Fact]
    public void CarrierStatsExcludeFirstSatelliteAndSatelliteEngines()
    {
        var t = new PartTree(Vessel());
        var p = t.PlanReleases(new[] { Sat(t, 10), Sat(t, 21), Sat(t, 30) });

        double total = Vessel().Sum(x => x.Mass);
        Assert.Equal(total - 0.35, p.CarrierMassAfterFirst, 1e-9);
        Assert.Equal(60.0,  p.CarrierThrust);
        Assert.Equal(345.0, p.CarrierIsp);
    }

    [Fact]
    public void FirstSatelliteNeedsNoEngine()
    {
        var parts = Vessel().Where(x => x.Id != 12).ToList();  // SAT-A without engine
        var t = new PartTree(parts);
        Assert.True(t.PlanReleases(new[] { Sat(t, 10), Sat(t, 21), Sat(t, 30) }).IsValid);
    }

    [Fact]
    public void LaterSatelliteWithoutEngineIsRejected()
    {
        var parts = Vessel().Where(x => x.Id != 12).ToList();
        var t = new PartTree(parts);
        var p = t.PlanReleases(new[] { Sat(t, 21), Sat(t, 10), Sat(t, 30) });
        Assert.False(p.IsValid);
        Assert.Contains("SAT-2", p.Error);
    }

    [Fact]
    public void CarrierWithoutActiveEngineIsRejected()
    {
        var parts = Vessel().Select(x => x.Id == 3
            ? new PartInfo(3, 2, "Carrier engine", mass: 1.0, thrust: 60.0, isp: 345.0, engineActive: false)
            : x).ToList();
        var t = new PartTree(parts);
        var p = t.PlanReleases(new[] { Sat(t, 10), Sat(t, 21), Sat(t, 30) });
        Assert.False(p.IsValid);
        Assert.Contains("carrier", p.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void ExactlyThreeSatellitesRequired(int count)
    {
        var t = new PartTree(Vessel());
        var all = t.Candidates();
        var order = Enumerable.Range(0, count).Select(i => all[i % all.Count]).ToList();
        Assert.False(t.PlanReleases(order).IsValid);
    }

    [Fact]
    public void DuplicateSatelliteRejected()
    {
        var t = new PartTree(Vessel());
        Assert.False(t.PlanReleases(new[] { Sat(t, 10), Sat(t, 10), Sat(t, 30) }).IsValid);
    }

    // Stacked satellites: carrier - dec(50) - probe(51) - engine(52) - dec(53) - probe(54) - engine(55)
    private static List<PartInfo> Stacked() => new()
    {
        new(1,  null, "Carrier core", isCommand: true, mass: 0.5),
        new(3,  1,    "Carrier engine", mass: 1.0, thrust: 60.0, isp: 345.0, engineActive: true),
        new(50, 1,  "Lower decoupler", SeparatorKind.Decoupler, decouplesFromParent: true, mass: 0.05),
        new(51, 50, "Lower probe", isCommand: true, mass: 0.1),
        new(52, 51, "Lower engine", mass: 0.2, thrust: 2.0, isp: 300.0),
        new(53, 52, "Upper decoupler", SeparatorKind.Decoupler, decouplesFromParent: true, mass: 0.05),
        new(54, 53, "Upper probe", isCommand: true, mass: 0.1),
        new(55, 54, "Upper engine", mass: 0.2, thrust: 2.0, isp: 300.0),
        new(60, 1,  "Side decoupler", SeparatorKind.Decoupler, decouplesFromParent: true, mass: 0.05),
        new(61, 60, "Side probe", isCommand: true, mass: 0.1),
        new(62, 61, "Side engine", mass: 0.2, thrust: 2.0, isp: 300.0),
    };

    [Fact]
    public void NestedSatelliteMustBeReleasedFirst()
    {
        var t = new PartTree(Stacked());
        var p = t.PlanReleases(new[] { Sat(t, 50), Sat(t, 53), Sat(t, 60) });
        Assert.False(p.IsValid);
        Assert.Contains("Upper probe", p.Error);
    }

    [Fact]
    public void NestedReleasedFirstLeavesOnlyOuterPartsForContainer()
    {
        var t = new PartTree(Stacked());
        var p = t.PlanReleases(new[] { Sat(t, 53), Sat(t, 50), Sat(t, 60) });
        Assert.True(p.IsValid, p.Error);
        Assert.Equal(0.35, p.Satellites[0].Mass, 1e-9);  // upper: 53, 54, 55
        Assert.Equal(0.35, p.Satellites[1].Mass, 1e-9);  // lower: 50, 51, 52 only
    }

    // ── Docking ports joined in the editor ("PreAttached") ────────────────────

    // Layout of the Steam test vessel: each satellite sits on a single Jr port
    // mounted on a fairing base; the port's docking face holds the satellite.
    //   core(1) - fairing(2) - port(3) - engine(4) - probe(5)
    //                        - fairing(6) - port(7) - engine(8) - probe(9)
    private static List<PartInfo> SinglePorts() => new()
    {
        new(1, null, "Carrier core", isCommand: true, mass: 1.0, thrust: 60.0, isp: 345.0, engineActive: true),
        new(2, 1, "Fairing base", mass: 0.1),
        new(3, 2, "Jr port A", SeparatorKind.DockingPort, decoupledChildId: 4, mass: 0.02),
        new(4, 3, "Spark A", mass: 0.1, thrust: 20.0, isp: 320.0),
        new(5, 4, "HECS2 A", isCommand: true, mass: 0.1),
        new(6, 2, "Fairing base 2", mass: 0.1),
        new(7, 6, "Jr port B", SeparatorKind.DockingPort, decoupledChildId: 8, mass: 0.02),
        new(8, 7, "Spark B", mass: 0.1, thrust: 20.0, isp: 320.0),
        new(9, 8, "HECS2 B", isCommand: true, mass: 0.1),
    };

    [Fact]
    public void SinglePortReleasesWhatItsDockingFaceHolds()
    {
        var t = new PartTree(SinglePorts());
        var a = Sat(t, 3);
        Assert.Equal(new uint[] { 4, 5 }, a.PartIds.OrderBy(i => i));  // the port stays on the carrier
        Assert.Equal("HECS2 A", a.Name);
        Assert.Equal(new uint[] { 3, 7 }, t.Candidates().Select(c => c.SeparatorId).OrderBy(i => i));
    }

    [Fact]
    public void SinglePortFacingParentLeavesWithTheSatellite()
    {
        // Port mounted upside down: its docking face is on the carrier side.
        var parts = new List<PartInfo>
        {
            new(1, null, "Carrier core", isCommand: true, mass: 1.0),
            new(2, 1, "Jr port", SeparatorKind.DockingPort, decouplesFromParent: true, mass: 0.02),
            new(3, 2, "Probe", isCommand: true, mass: 0.1),
        };
        Assert.Equal(new uint[] { 2, 3 }, Sat(new PartTree(parts), 2).PartIds.OrderBy(i => i));
    }

    [Fact]
    public void EditorPortPairGivesOneSatellite()
    {
        // Both ports of a pair joined in the editor can fire; they release the same satellite.
        var parts = new List<PartInfo>
        {
            new(1, null, "Carrier core", isCommand: true, mass: 1.0),
            new(2, 1, "Inner port", SeparatorKind.DockingPort, decoupledChildId: 3, mass: 0.02),
            new(3, 2, "Outer port", SeparatorKind.DockingPort, decouplesFromParent: true, mass: 0.02),
            new(4, 3, "Probe", isCommand: true, mass: 0.1),
        };
        var c = Assert.Single(new PartTree(parts).Candidates());
        Assert.Equal(new uint[] { 3, 4 }, c.PartIds.OrderBy(i => i));
    }

    [Fact]
    public void ReleasedByFollowsEachDecouplersFarSide()
    {
        var t = new PartTree(Vessel());
        // Decoupler A leaves with its satellite; decoupler C drops its child's side.
        Assert.Equal(new uint[] { 10, 11, 12 }, t.ReleasedBy(new uint[] { 10 }).OrderBy(i => i));
        Assert.Equal(new uint[] { 31, 32 }, t.ReleasedBy(new uint[] { 30 }).OrderBy(i => i));
        // A tank and an engine release nothing.
        Assert.Empty(t.ReleasedBy(new uint[] { 2, 3 }));
    }

    [Fact]
    public void ACarrierDecouplerCanCutLooseTheSatellites()
    {
        // The user's craft: the carrier's engine plate (a decoupler in a carrier stage)
        // with the satellites hanging on its far side.
        var parts = new List<PartInfo>
        {
            new(1, null, "Carrier core", isCommand: true),
            new(2, 1,    "Engine plate", SeparatorKind.Decoupler, decouplesFromParent: true),
            new(3, 2,    "Carrier engine", thrust: 60.0, isp: 345.0, engineActive: true),
            new(4, 2,    "Port", SeparatorKind.DockingPort),
            new(5, 4,    "Probe", isCommand: true),
        };
        var released = new PartTree(parts).ReleasedBy(new uint[] { 2, 3 });
        Assert.Contains(5u, released);
        Assert.Equal(StagingDecision.WouldReleaseSatellite,
                     StagingGuard.Decide(2, new uint[] { 2, 3 }, new HashSet<uint> { 4, 5 }, 0, released));
    }
}
