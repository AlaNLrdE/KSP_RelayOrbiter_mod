namespace RelayOrbiter.Core.Tests;

public static class Precision
{
    // C# and C++ use the same IEEE 754 doubles, but Math.Pow/Cbrt may differ from
    // libm in the last few ulps, so compare with a tight relative tolerance.
    public const double RelTol = 1e-12;

    public static void Close(double expected, double actual, string what)
    {
        double tol = RelTol * Math.Max(Math.Abs(expected), 1.0);
        Assert.True(Math.Abs(expected - actual) <= tol,
            $"{what}: expected {expected:R}, actual {actual:R} (tolerance {tol:R})");
    }

    public static void Close(double? expected, double? actual, string what)
    {
        Assert.True(expected.HasValue == actual.HasValue,
            $"{what}: expected {(expected.HasValue ? expected.Value.ToString("R") : "null")}, " +
            $"actual {(actual.HasValue ? actual.Value.ToString("R") : "null")}");
        if (expected.HasValue) Close(expected.Value, actual!.Value, what);
    }
}
