namespace RelayOrbiter.Core.Tests;

public class AltitudePlannerTests
{
    private readonly KspBodyDatabase _db = new();
    private readonly AltitudePlanner _planner;

    public AltitudePlannerTests() =>
        _planner = new AltitudePlanner(_db, new OrbitalMechanics());

    private CelestialBody Body(string name) => _db.Find(name)!;

    [Fact]
    public void NullDependenciesThrow()
    {
        Assert.Throws<ArgumentNullException>(() => new AltitudePlanner(null!, new OrbitalMechanics()));
        Assert.Throws<ArgumentNullException>(() => new AltitudePlanner(_db, null!));
    }

    // ── Validate ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5_000.0)]
    public void ZeroOrNegativeAltitudeIsBelowSurface(double h) =>
        Assert.Equal(AltitudeStatus.BelowSurface, _planner.Validate(Body("Kerbin"), h));

    [Fact]
    public void AltitudeInsideAtmosphere() =>
        Assert.Equal(AltitudeStatus.InsideAtmosphere, _planner.Validate(Body("Kerbin"), 69_999.0));

    [Fact]
    public void AltitudeAtAtmosphereEdgeIsOk() =>
        Assert.Equal(AltitudeStatus.Ok, _planner.Validate(Body("Kerbin"), 70_000.0));

    [Fact]
    public void AltitudeAboveSoiExceedsSoi()
    {
        var kerbin = Body("Kerbin");
        Assert.Equal(AltitudeStatus.ExceedsSoi,
                     _planner.Validate(kerbin, kerbin.Soi!.Value - kerbin.Radius + 1.0));
    }

    [Fact]
    public void AltitudeAtSoiEdgeIsOk()
    {
        var kerbin = Body("Kerbin");
        Assert.Equal(AltitudeStatus.Ok, _planner.Validate(kerbin, kerbin.Soi!.Value - kerbin.Radius));
    }

    [Fact]
    public void AirlessBodyAcceptsLowAltitude() =>
        Assert.Equal(AltitudeStatus.Ok, _planner.Validate(Body("Mun"), 1_000.0));

    [Fact]
    public void KerbolHasNoSoiLimit() =>
        Assert.Equal(AltitudeStatus.Ok, _planner.Validate(Body("Kerbol"), 1e15));

    // ── Synchronous ───────────────────────────────────────────────────────────

    [Fact]
    public void SynchronousOptionForKerbin()
    {
        var o = _planner.Synchronous(Body("Kerbin"));
        Assert.Equal(AltitudeSource.Synchronous, o.Source);
        Assert.True(o.IsValid);
        Assert.Equal(2_863_334.0, o.Altitude, 0.5e3);
    }

    [Fact]
    public void SynchronousOrbitOfMunExceedsSoi()
    {
        var o = _planner.Synchronous(Body("Mun"));
        Assert.Equal(AltitudeStatus.ExceedsSoi, o.Status);
        Assert.False(o.IsValid);
    }

    [Theory]
    [MemberData(nameof(GoldenVectors.BodyNames), MemberType = typeof(GoldenVectors))]
    public void SynchronousMatchesGoldenVectors(string name)
    {
        // The C++ exporter emits a "sync" case exactly when the sync orbit is usable.
        var golden = GoldenVectors.Body(name).Results.SingleOrDefault(r => r.Case == "sync");
        var o = _planner.Synchronous(Body(name));

        Assert.Equal(golden is not null, o.IsValid);
        Precision.Close(GoldenVectors.Body(name).SyncAltitude!.Value, o.Altitude, "sync altitude");
    }

    // ── Children ──────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(GoldenVectors.BodyNames), MemberType = typeof(GoldenVectors))]
    public void ChildrenMatchGoldenVectors(string name)
    {
        var golden = GoldenVectors.Body(name).Results.Where(r => r.Case.StartsWith("child:")).ToList();
        var options = _planner.Children(Body(name));

        Assert.Equal(golden.Count, options.Count);
        for (int i = 0; i < golden.Count; ++i)
        {
            Assert.Equal(AltitudeSource.Child, options[i].Source);
            Assert.Equal(golden[i].Case.Substring("child:".Length), options[i].Name);
            Precision.Close(golden[i].HTarget, options[i].Altitude, $"{options[i].Name} altitude");
            Assert.True(options[i].IsValid);
        }
    }

    // ── Midpoint ──────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(GoldenVectors.BodyNames), MemberType = typeof(GoldenVectors))]
    public void MidpointMatchesGoldenVectors(string name)
    {
        var golden = GoldenVectors.Body(name).Results.SingleOrDefault(r => r.Case.StartsWith("mid:"));
        if (golden is null) return;  // fewer than two children

        var kids = _db.ChildrenOf(name);
        var o = _planner.Midpoint(Body(name), kids[0], kids[1]);

        Assert.Equal(AltitudeSource.Midpoint, o.Source);
        Assert.Equal(golden.Case.Substring("mid:".Length), o.Name);
        Precision.Close(golden.HTarget, o.Altitude, "midpoint altitude");
        Assert.True(o.IsValid);
    }

    [Fact]
    public void MidpointIsSymmetric()
    {
        var a = _planner.Midpoint(Body("Kerbol"), Body("Kerbin"), Body("Duna"));
        var b = _planner.Midpoint(Body("Kerbol"), Body("Duna"), Body("Kerbin"));
        Assert.Equal(a.Altitude, b.Altitude);
    }

    // ── Custom ────────────────────────────────────────────────────────────────

    [Fact]
    public void CustomOptionCarriesAltitudeAndStatus()
    {
        var ok = _planner.Custom(Body("Kerbin"), 500_000.0);
        Assert.Equal(AltitudeSource.Custom, ok.Source);
        Assert.Equal(500_000.0, ok.Altitude);
        Assert.True(ok.IsValid);

        Assert.Equal(AltitudeStatus.InsideAtmosphere, _planner.Custom(Body("Kerbin"), 10_000.0).Status);
    }
}
