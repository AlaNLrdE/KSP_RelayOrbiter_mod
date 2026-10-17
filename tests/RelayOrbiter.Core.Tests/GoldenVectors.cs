using System.Text.Json;
using System.Text.Json.Serialization;
using RelayOrbiter.Core;

namespace RelayOrbiter.Core.Tests;

// Golden test vectors exported by the C++ reference calculator
// (KSP_RelayOrbiter_calculator/tools/export_vectors.cpp).
public static class GoldenVectors
{
    private static readonly Lazy<GoldenFile> File = new(Load);

    public static IReadOnlyList<GoldenBody> Bodies => File.Value.Bodies;

    public static GoldenBody Body(string name) => Bodies.Single(b => b.Name == name);

    // xUnit member data: one row per body name.
    public static IEnumerable<object[]> BodyNames() =>
        Bodies.Select(b => new object[] { b.Name });

    // xUnit member data: one row per (body name, result index).
    public static IEnumerable<object[]> Cases() =>
        Bodies.SelectMany(b => b.Results.Select((_, i) => new object[] { b.Name, i }));

    private static GoldenFile Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "golden_vectors.json");
        return JsonSerializer.Deserialize<GoldenFile>(System.IO.File.ReadAllText(path))
               ?? throw new InvalidDataException($"Could not parse {path}");
    }
}

public sealed record GoldenFile(
    [property: JsonPropertyName("generator")] string Generator,
    [property: JsonPropertyName("bodies")]    List<GoldenBody> Bodies);

public sealed record GoldenBody(
    [property: JsonPropertyName("name")]             string  Name,
    [property: JsonPropertyName("radius")]           double  Radius,
    [property: JsonPropertyName("mu")]               double  Mu,
    [property: JsonPropertyName("atm")]              double  Atm,
    [property: JsonPropertyName("rot_period")]       double  RotPeriod,
    [property: JsonPropertyName("soi")]              double? Soi,
    [property: JsonPropertyName("orbital_radius")]   double? OrbitalRadius,
    [property: JsonPropertyName("parent")]           string? Parent,
    [property: JsonPropertyName("sync_altitude")]    double? SyncAltitude,
    [property: JsonPropertyName("soi_max_altitude")] double? SoiMaxAltitude,
    [property: JsonPropertyName("results")]          List<GoldenResult> Results)
{
    public CelestialBody ToCelestialBody() =>
        new(Name, Radius, Mu, Atm, RotPeriod, Soi, OrbitalRadius, Parent);
}

public sealed record GoldenResult(
    [property: JsonPropertyName("case")]          string  Case,
    [property: JsonPropertyName("h_target")]      double  HTarget,
    [property: JsonPropertyName("r_c")]           double  RC,
    [property: JsonPropertyName("T_c")]           double  TC,
    [property: JsonPropertyName("v_c")]           double  VC,
    [property: JsonPropertyName("T_e")]           double  TE,
    [property: JsonPropertyName("a_e")]           double  AE,
    [property: JsonPropertyName("r_apo")]         double  RApo,
    [property: JsonPropertyName("h_apo")]         double  HApo,
    [property: JsonPropertyName("r_peri")]        double  RPeri,
    [property: JsonPropertyName("h_peri")]        double  HPeri,
    [property: JsonPropertyName("v_apo")]         double  VApo,
    [property: JsonPropertyName("v_peri")]        double  VPeri,
    [property: JsonPropertyName("surface_ok")]    bool    SurfaceOk,
    [property: JsonPropertyName("atm_ok")]        bool    AtmOk,
    [property: JsonPropertyName("min_h_surface")] double  MinHSurface,
    [property: JsonPropertyName("min_h_atm")]     double? MinHAtm);
