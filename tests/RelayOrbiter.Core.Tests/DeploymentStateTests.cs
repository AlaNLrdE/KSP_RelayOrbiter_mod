using System.Globalization;

namespace RelayOrbiter.Core.Tests;

public class DeploymentStateTests
{
    private static DeploymentState Sample() => new()
    {
        Step = 17, Carrier = 4_000_000_001u,
        Separators = new uint[] { 11, 22, 33 },
        CommandParts = new uint[] { 12, 23, 34 },
        SatelliteParts = new[] { new uint[] { 12, 13 }, new uint[] { 23 }, new uint[] { 34, 35, 36 } },
        Names = new[] { "Kerbin Orion 1/3 - SYNC i10", "Kerbin Orion 2/3 - SYNC i10", "Kerbin Orion 3/3 - SYNC i10" },
        ExtendPanels = true, TopUp = false, Parking = ParkingOrbit.Outer,
        GoalRadius = 3_463_334.25, GoalInclination = 10.5, ParkingUT = 794_438_374.123,
        LastReleased = 1, CarrierBurn = 4.25, SatelliteBurns = new[] { 9.5, 12.75 },
        Transfer = true, PlaneChange = false, PlaneChangeFirst = true,
        SavedLoad = 2250f, SavedUnload = 2500f,
    };

    private static void AssertSame(DeploymentState a, DeploymentState b)
    {
        Assert.Equal(a.Step, b.Step);
        Assert.Equal(a.Carrier, b.Carrier);
        Assert.Equal(a.Separators, b.Separators);
        Assert.Equal(a.CommandParts, b.CommandParts);
        Assert.Equal(a.SatelliteParts, b.SatelliteParts);
        Assert.Equal(a.Names, b.Names);
        Assert.Equal(a.ExtendPanels, b.ExtendPanels);
        Assert.Equal(a.TopUp, b.TopUp);
        Assert.Equal(a.Parking, b.Parking);
        Assert.Equal(a.GoalRadius, b.GoalRadius);
        Assert.Equal(a.GoalInclination, b.GoalInclination);
        Assert.Equal(a.ParkingUT, b.ParkingUT);
        Assert.Equal(a.LastReleased, b.LastReleased);
        Assert.Equal(a.CarrierBurn, b.CarrierBurn);
        Assert.Equal(a.SatelliteBurns, b.SatelliteBurns);
        Assert.Equal(a.Transfer, b.Transfer);
        Assert.Equal(a.PlaneChange, b.PlaneChange);
        Assert.Equal(a.PlaneChangeFirst, b.PlaneChangeFirst);
        Assert.Equal(a.SavedLoad, b.SavedLoad);
        Assert.Equal(a.SavedUnload, b.SavedUnload);
    }

    private static DeploymentState RoundTrip(DeploymentState s) =>
        DeploymentState.FromValues(new Dictionary<string, string>(s.ToValues()));

    [Fact]
    public void RoundTripKeepsEverything() => AssertSame(Sample(), RoundTrip(Sample()));

    [Fact]
    public void RoundTripWithoutNamesOrInclination()
    {
        var s = Sample();
        s.Names = null;
        s.GoalInclination = null;
        s.ParkingUT = double.NaN;
        var r = RoundTrip(s);
        Assert.Null(r.Names);
        Assert.Null(r.GoalInclination);
        Assert.True(double.IsNaN(r.ParkingUT));
    }

    [Fact]
    public void NumbersDoNotDependOnTheCulture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("es-ES");  // decimal comma
            var values = Sample().ToValues();
            Assert.DoesNotContain(",", values["GoalRadius"]);
            AssertSame(Sample(), DeploymentState.FromValues(new Dictionary<string, string>(values)));
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void NamesMayContainSeparators()
    {
        var s = Sample();
        s.Names = new[] { "A, B; C | D", "x", "y" };
        Assert.Equal(s.Names, RoundTrip(s).Names);
    }

    [Fact]
    public void MissingValuesAreRejected() =>
        Assert.Throws<FormatException>(() => DeploymentState.FromValues(new Dictionary<string, string>()));
}
