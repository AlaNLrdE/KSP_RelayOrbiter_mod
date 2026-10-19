namespace RelayOrbiter.Core.Tests;

public class EncounterCostTests
{
    // Duna: radius 320 km; the plan aimed at a 1,000 km periapsis.
    private const double DunaR = 320_000, Target = DunaR + 1_000_000, MinPe = DunaR + 60_000;

    private static double Cost(double peAltitude, double inclination = 5.0) =>
        EncounterCost.Of(DunaR + peAltitude, inclination, Target, MinPe);

    [Fact]
    public void FarOffArrivalsStillShowWhichIsBetter()
    {
        // The Duna run: 23,840 km and 47,526 km both cost 1 under the old cap, so the
        // midcourse correction found nothing better than 47,526 km.
        double c23 = Cost(23_840_000), c47 = Cost(47_526_000), c5 = Cost(5_000_000);
        Assert.True(c5 < c23 && c23 < c47);
        Assert.True(c47 < 1.0, "still an encounter");
    }

    [Fact]
    public void SmallErrorsKeepTheirMeaning()
    {
        // Within the 3% correction tolerance a 2% error stays under it.
        Assert.True(Cost(1_026_400) < 0.03);
        Assert.Equal(0.0, Cost(1_000_000));
    }

    [Fact]
    public void AGoalInclinationIsAimedAt()
    {
        // Points of the Duna run's correction search, goal 0°: 978 km at 24.9° was taken,
        // and the capture then needed a 209 m/s plane change.
        double taken = EncounterCost.Of(DunaR + 978_300, 24.94, Target, MinPe, 0.0);
        double equatorial = EncounterCost.Of(DunaR + 1_000_000, 0.44, Target, MinPe, 0.0);
        Assert.True(equatorial < taken);
        Assert.True(taken > 0.03, "24.9° off: not on course");
        Assert.True(equatorial < 0.03, "on course");
    }

    [Fact]
    public void WithoutAGoalTheArrivalPlaneIsFree() =>
        Assert.Equal(EncounterCost.Of(DunaR + 1_000_000, 0.4, Target, MinPe),
                     EncounterCost.Of(DunaR + 1_000_000, 71.0, Target, MinPe));

    [Fact]
    public void ARetrogradeGoalWantsARetrogradeArrival()
    {
        // The Mun constellation at 135°: arriving retrograde is right, prograde the wrong way.
        double retro = EncounterCost.Of(DunaR + 1_000_000, 130.0, Target, MinPe, 135.0);
        double pro = EncounterCost.Of(DunaR + 1_000_000, 45.0, Target, MinPe, 135.0);
        Assert.True(retro < 1.0);
        Assert.True(pro >= EncounterCost.RetrogradePenalty);
        Assert.False(EncounterCost.WrongWay(95.0, 90.0));  // a polar goal: either way
    }

    [Fact]
    public void BandsStayOrdered()
    {
        double encounter = Cost(40_000_000), impact = Cost(10_000), retrograde = Cost(1_000_000, 150.0);
        Assert.True(encounter < 1.0);
        Assert.InRange(impact, 2.0, 3.0);
        Assert.True(retrograde >= EncounterCost.RetrogradePenalty);
        Assert.True(retrograde < 10.0);  // below "no encounter"
    }
}
