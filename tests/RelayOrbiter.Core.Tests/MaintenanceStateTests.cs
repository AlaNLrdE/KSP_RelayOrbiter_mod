namespace RelayOrbiter.Core.Tests;

public class MaintenanceStateTests
{
    private static MaintenanceState Sample(int? reference = null)
    {
        var fit = StationKeeping.Fit(new[] { 0.0, 125.0, 240.0 }, 3);
        return MaintenanceState.Start(new uint[] { 11, 22, 33 }, fit, 1000.0, 2_200_000.0, 80_335.0,
                                      reference, 2, 230_000.0, double.MaxValue);
    }

    // ── Frame ─────────────────────────────────────────────────────────────────

    [Fact]
    public void SlotsAdvanceAtTheTargetPeriod()
    {
        var f = new SlotFrame(3, 10.0, 0.0, 1000.0);
        Precision.Close(10.0, f.SlotLongitudeDeg(0, 0.0), "start");
        Precision.Close(100.0, f.SlotLongitudeDeg(0, 250.0), "quarter orbit");
        Precision.Close(220.0, f.SlotLongitudeDeg(1, 250.0), "next slot");
        Precision.Close(10.0, f.SlotLongitudeDeg(0, 3000.0), "whole orbits");
    }

    [Fact]
    public void ErrorIsAgainstTheMovingSlot()
    {
        var f = new SlotFrame(4, 0.0, 0.0, 1000.0);
        // A quarter orbit later slot 1 is at 180°; a satellite at 185° is 5° ahead.
        Precision.Close(5.0, f.ErrorDeg(185.0, 1, 250.0), "ahead");
        Precision.Close(-5.0, f.ErrorDeg(175.0, 1, 250.0), "behind");
    }

    [Fact]
    public void ASatelliteOnTheTargetPeriodKeepsItsError()
    {
        var f = new SlotFrame(3, 0.0, 0.0, 1000.0);
        double Lon(double t) => 7.0 + 360.0 * t / 1000.0;  // 7° ahead of slot 0, same period
        Precision.Close(7.0, f.ErrorDeg(Lon(0.0), 0, 0.0), "now");
        Precision.Close(7.0, f.ErrorDeg(Lon(12_345.0), 0, 12_345.0), "later");
    }

    // ── State ─────────────────────────────────────────────────────────────────

    [Fact]
    public void StartsEverySatelliteAtTheTransferStage()
    {
        var s = Sample();
        Assert.All(s.Stage, st => Assert.Equal(CorrectionStage.Transfer, st));
        Assert.All(s.PhaseUT, ut => Assert.True(double.IsNaN(ut)));
        Assert.False(s.AllDone);
    }

    [Fact]
    public void TheReferenceIsDoneFromTheStart()
    {
        var s = Sample(reference: 0);
        Assert.Equal(CorrectionStage.Done, s.Stage[0]);
        Assert.Equal(CorrectionStage.Transfer, s.Stage[1]);
    }

    [Fact]
    public void StagesGoInOrder()
    {
        var s = Sample();
        var seen = new List<CorrectionStage>();
        while (s.Stage[1] != CorrectionStage.Done)
        {
            seen.Add(s.Stage[1]);
            s.Advance(1, phaseOk: true);
        }
        Assert.Equal(new[] { CorrectionStage.Transfer, CorrectionStage.Circularize, CorrectionStage.PlaneLate, CorrectionStage.Apsis,
                             CorrectionStage.Phase, CorrectionStage.PhaseReturn, CorrectionStage.Trim }, seen);
    }

    [Fact]
    public void PhasesAgainOnceWhenStillOffAfterTheTrim()
    {
        var s = Sample();
        s.Stage[1] = CorrectionStage.Phase;
        s.Advance(1, false);                        // phase → return (attempt 1)
        s.Advance(1, false);                        // return → trim
        s.PhaseUT[1] = 5.0;
        s.Advance(1, phaseOk: false);               // still off: phase again
        Assert.Equal(CorrectionStage.Phase, s.Stage[1]);
        Assert.True(double.IsNaN(s.PhaseUT[1]));
        s.Advance(1, false); s.Advance(1, false);   // attempt 2
        s.Advance(1, phaseOk: false);               // give up
        Assert.Equal(CorrectionStage.Done, s.Stage[1]);
    }

    [Fact]
    public void AllDoneWhenEveryStageIsDone()
    {
        var s = Sample();
        for (int i = 0; i < 3; ++i) s.Stage[i] = CorrectionStage.Done;
        Assert.True(s.AllDone);
    }

    [Fact]
    public void RoundTripsThroughTheSaveFile()
    {
        var s = Sample(reference: 2);
        s.Stage[1] = CorrectionStage.PhaseReturn;
        s.PhaseUT[1] = 123_456.789;
        s.PhaseOrbits[1] = 4;
        s.Attempts[1] = 1;
        s.Current = 1;
        var r = MaintenanceState.FromValues(new Dictionary<string, string>(s.ToValues()));
        Assert.Equal(s.Members, r.Members);
        Assert.Equal(s.SlotOf, r.SlotOf);
        Assert.Equal(s.Stage, r.Stage);
        Assert.Equal(s.PhaseOrbits, r.PhaseOrbits);
        Assert.Equal(s.Attempts, r.Attempts);
        Assert.Equal(1, r.Current);
        Assert.Equal(2, r.Reference);
        Assert.Equal(s.OffsetDeg, r.OffsetDeg);
        Assert.Equal(123_456.789, r.PhaseUT[1]);
        Assert.True(double.IsNaN(r.PhaseUT[0]));
        Assert.Equal(double.MaxValue, r.MaxRadius);
        Assert.Equal(s.TargetPeriod, r.TargetPeriod);
    }

    [Fact]
    public void RejectsInconsistentLists()
    {
        var v = new Dictionary<string, string>(Sample().ToValues()) { ["Stage"] = "Done" };
        Assert.Throws<FormatException>(() => MaintenanceState.FromValues(v));
    }

    [Fact]
    public void RejectsMissingValues()
    {
        var v = new Dictionary<string, string>(Sample().ToValues());
        v.Remove("Slots");
        Assert.Throws<FormatException>(() => MaintenanceState.FromValues(v));
    }

    [Fact]
    public void ProgressCountsBurnsDoneAgainstThoseThatMayBeLeft()
    {
        var s = Sample(reference: 0);           // two satellites to correct, 6 burns each at most
        Precision.Close(0.0, s.Progress, "start");
        s.Stage[1] = CorrectionStage.Done;
        s.BurnsDone = 3;
        Precision.Close(3.0 / 9.0, s.Progress, "one done");
        Assert.Equal(9, s.BurnsEstimate);
        s.Stage[2] = CorrectionStage.Done;
        Precision.Close(1.0, s.Progress, "all done");
    }

    [Fact]
    public void BurnsDoneRoundTrips()
    {
        var s = Sample();
        s.BurnsDone = 7;
        Assert.Equal(7, MaintenanceState.FromValues(new Dictionary<string, string>(s.ToValues())).BurnsDone);
    }

    [Fact]
    public void MatchingPlanesStartsWithThePlaneStage()
    {
        var fit = StationKeeping.Fit(new[] { 0.0, 120.0, 240.0 }, 3);
        var s = MaintenanceState.Start(new uint[] { 1, 2, 3 }, fit, 0.0, 2e6, 6e4, 0, 1, 2e5, 3e6, new OrbitPlane(1.5, 30));
        Assert.Equal(CorrectionStage.Done, s.Stage[0]);         // the reference
        Assert.Equal(CorrectionStage.Plane, s.Stage[1]);
        s.Advance(1, true);
        Assert.Equal(CorrectionStage.Transfer, s.Stage[1]);
        var r = MaintenanceState.FromValues(new Dictionary<string, string>(s.ToValues()));
        Assert.True(r.MatchPlanes);
        Assert.Equal(1.5, r.PlaneInclination);
        Assert.Equal(30.0, r.PlaneLan);
    }

    [Fact]
    public void StatesSavedBeforePlaneMatchingStillLoad()
    {
        var v = new Dictionary<string, string>(Sample().ToValues());
        v.Remove("MatchPlanes"); v.Remove("PlaneInclination"); v.Remove("PlaneLan");
        Assert.False(MaintenanceState.FromValues(v).MatchPlanes);
    }

    // ── Shapes ────────────────────────────────────────────────────────────────

    private static MaintenanceState Shaped(string? anchor = null)
    {
        var spec = ConstellationShapes.WalkerDelta(4, 2, 1, 45, 3e6);
        var members = ConstellationShapes.Slots(spec)
            .Select(x => new ShapeMember(x.InclinationDeg, StationKeeping.Mod360(20 + x.LanOffsetDeg),
                                         StationKeeping.Mod360(5 + x.PhaseOffsetDeg)))
            .ToList();
        var fit = ShapeFit.Fit(spec, members);
        return MaintenanceState.StartShaped(new uint[] { 1, 2, 3, 4 }, fit, 100.0, 5000.0, null, 1, 7e5, 8e7, anchor);
    }

    [Fact]
    public void ShapedStateKeepsEveryMembersTarget()
    {
        var s = Shaped();
        Assert.True(s.Shaped);
        Assert.True(s.MatchPlanes);
        Assert.All(s.Stage, st => Assert.Equal(CorrectionStage.Plane, st));
        Assert.Equal(2, s.TargetLan.Select(l => Math.Round(l, 6)).Distinct().Count());
        Assert.All(s.TargetInc, inc => Assert.Equal(45.0, inc, 6));
        Assert.Equal(new OrbitPlane(45, s.TargetLan[1]).LanDeg, s.TargetPlane(1).LanDeg);
        Assert.False(s.TargetElliptical(0));
    }

    [Fact]
    public void ShapedSlotPhaseAdvancesWithItsOffset()
    {
        var s = Shaped();
        int i = Array.IndexOf(s.SlotOf, s.SlotOf.First(x => s.PhaseOffset[Array.IndexOf(s.SlotOf, x)] > 0));
        double atEpoch = s.SlotPhaseDeg(i, 100.0);
        Precision.Close(StationKeeping.Mod360(s.OffsetDeg + s.PhaseOffset[i]), atEpoch, "epoch");
        Precision.Close(StationKeeping.Mod360(atEpoch + 90.0), s.SlotPhaseDeg(i, 100.0 + 1250.0), "quarter period");
    }

    [Fact]
    public void TrojanSlotsFollowTheMoonsPhase()
    {
        var s = Shaped("Mun");
        Precision.Close(StationKeeping.Mod360(30.0 + s.PhaseOffset[0]), s.SlotPhaseDeg(0, 9999.0, anchorPhase: 30.0), "anchor");
    }

    [Fact]
    public void ShapedStateRoundTrips()
    {
        var s = Shaped("Mun");
        s.Stage[2] = CorrectionStage.Apsis;
        var r = MaintenanceState.FromValues(new Dictionary<string, string>(s.ToValues()));
        Assert.True(r.Shaped);
        Assert.Equal(s.TargetLan, r.TargetLan);
        Assert.Equal(s.PhaseOffset, r.PhaseOffset);
        Assert.Equal(s.TargetSmas, r.TargetSmas);
        Assert.Equal("Mun", r.AnchorBody);
        Assert.Equal(CorrectionStage.Apsis, r.Stage[2]);
    }

    [Fact]
    public void ApsisComesBetweenCircularizeAndPhase()
    {
        var s = Shaped();
        s.Stage[0] = CorrectionStage.Circularize;
        s.Advance(0, true);
        Assert.Equal(CorrectionStage.PlaneLate, s.Stage[0]);
        s.Advance(0, true);
        Assert.Equal(CorrectionStage.Apsis, s.Stage[0]);
        s.Advance(0, true);
        Assert.Equal(CorrectionStage.Phase, s.Stage[0]);
    }
}
