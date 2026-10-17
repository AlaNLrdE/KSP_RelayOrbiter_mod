namespace RelayOrbiter.Core.Tests;

public class KspBodyDatabaseTests
{
    private readonly KspBodyDatabase _db = new();

    [Fact]
    public void ContainsAllSeventeenBodies() =>
        Assert.Equal(17, _db.All().Count);

    [Fact]
    public void ContainsSameBodiesAsGoldenVectors() =>
        Assert.Equal(GoldenVectors.Bodies.Select(b => b.Name).OrderBy(n => n),
                     _db.All().Select(b => b.Name).OrderBy(n => n));

    [Fact]
    public void FindUnknownBodyReturnsNull()
    {
        Assert.Null(_db.Find("Mars"));
        Assert.Null(_db.Find(""));
    }

    [Theory]
    [MemberData(nameof(GoldenVectors.BodyNames), MemberType = typeof(GoldenVectors))]
    public void BodyDataMatchesGoldenVectors(string name)
    {
        var expected = GoldenVectors.Body(name);
        var body = _db.Find(name);

        Assert.NotNull(body);
        Assert.Equal(expected.Name, body!.Name);
        Assert.Equal(expected.Radius, body.Radius);
        Assert.Equal(expected.Mu, body.Mu);
        Assert.Equal(expected.Atm, body.Atm);
        Assert.Equal(expected.RotPeriod, body.RotPeriod);
        Assert.Equal(expected.Soi, body.Soi);
        Assert.Equal(expected.OrbitalRadius, body.OrbitalRadius);
        Assert.Equal(expected.Parent, body.Parent);
    }

    [Theory]
    [MemberData(nameof(GoldenVectors.BodyNames), MemberType = typeof(GoldenVectors))]
    public void ChildrenMatchGoldenChildCases(string name)
    {
        // The exporter emits one "child:<name>" case per child, in ChildrenOf order.
        var expected = GoldenVectors.Body(name).Results
            .Where(r => r.Case.StartsWith("child:"))
            .Select(r => r.Case.Substring("child:".Length));

        Assert.Equal(expected, _db.ChildrenOf(name).Select(c => c.Name));
    }

    [Fact]
    public void ChildrenOfKerbolInOrbitalOrder() =>
        Assert.Equal(new[] { "Moho", "Eve", "Kerbin", "Duna", "Dres", "Jool", "Eeloo" },
                     _db.ChildrenOf("Kerbol").Select(c => c.Name));
}
