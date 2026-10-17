namespace RelayOrbiter.Core.Tests;

public class ResourceTopUpTests
{
    private static TankInfo Tank(uint part, string res, double amount, double max) => new(part, res, amount, max);

    [Fact]
    public void FullSatelliteNeedsNoTransfer() =>
        Assert.Empty(ResourceTopUp.Plan(
            new[] { Tank(10, "LiquidFuel", 90, 90) },
            new[] { Tank(1, "LiquidFuel", 500, 1000) }));

    [Fact]
    public void FillsFromTheLargestCarrierTankFirst()
    {
        var t = ResourceTopUp.Plan(
            new[] { Tank(10, "LiquidFuel", 30, 90) },
            new[] { Tank(1, "LiquidFuel", 100, 200), Tank(2, "LiquidFuel", 400, 1000), Tank(3, "LiquidFuel", 50, 50) });

        var only = Assert.Single(t);
        Assert.Equal(2u, only.From);
        Assert.Equal(10u, only.To);
        Assert.Equal("LiquidFuel", only.Resource);
        Assert.Equal(60.0, only.Amount, 1e-9);
    }

    [Fact]
    public void MovesOnToTheNextLargestWhenOneRunsDry()
    {
        var t = ResourceTopUp.Plan(
            new[] { Tank(10, "LiquidFuel", 0, 90) },
            new[] { Tank(1, "LiquidFuel", 40, 200), Tank(2, "LiquidFuel", 20, 1000) });

        Assert.Equal(new[] { (2u, 20.0), (1u, 40.0) }, t.Select(x => (x.From, x.Amount)));
    }

    [Fact]
    public void PartialFillWhenTheCarrierRunsOut()
    {
        var t = ResourceTopUp.Plan(
            new[] { Tank(10, "LiquidFuel", 0, 90) },
            new[] { Tank(1, "LiquidFuel", 25, 1000) });

        Assert.Equal(25.0, Assert.Single(t).Amount, 1e-9);
        Assert.Equal(65.0, ResourceTopUp.Shortfall(
            new[] { Tank(10, "LiquidFuel", 0, 90) }, t), 1e-9);
    }

    [Fact]
    public void ResourcesAreIndependent()
    {
        var t = ResourceTopUp.Plan(
            new[] { Tank(10, "LiquidFuel", 0, 90), Tank(10, "Oxidizer", 0, 110) },
            new[] { Tank(1, "LiquidFuel", 500, 1000), Tank(1, "Oxidizer", 500, 1000) });

        Assert.Equal(90.0,  t.Single(x => x.Resource == "LiquidFuel").Amount, 1e-9);
        Assert.Equal(110.0, t.Single(x => x.Resource == "Oxidizer").Amount, 1e-9);
    }

    [Fact]
    public void SharesTheCarrierFuelAcrossSeveralSatelliteTanks()
    {
        var t = ResourceTopUp.Plan(
            new[] { Tank(10, "LiquidFuel", 0, 50), Tank(20, "LiquidFuel", 0, 50) },
            new[] { Tank(1, "LiquidFuel", 70, 1000) });

        Assert.Equal(70.0, t.Sum(x => x.Amount), 1e-9);
        Assert.Equal(50.0, t.Where(x => x.To == 10).Sum(x => x.Amount), 1e-9);
    }

    [Fact]
    public void IgnoresCarrierTanksOfOtherResources() =>
        Assert.Empty(ResourceTopUp.Plan(
            new[] { Tank(10, "MonoPropellant", 0, 20) },
            new[] { Tank(1, "LiquidFuel", 500, 1000) }));

    [Fact]
    public void FillFractionOfTanks()
    {
        Assert.Equal(0.5, ResourceTopUp.FillFraction(new[] { Tank(10, "LiquidFuel", 45, 90) }), 1e-9);
        Assert.Equal(1.0, ResourceTopUp.FillFraction(Array.Empty<TankInfo>()), 1e-9);
    }
}
