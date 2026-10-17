namespace RelayOrbiter.Core
{
    // Builds a CelestialBody from the raw values KSP exposes on its own body objects,
    // so the calculator works with whatever planets the game has (stock, Kopernicus, OPM...).
    public static class BodyFactory
    {
        // parentName and semiMajorAxis are null for the star at the root of the system.
        // KSP reports the star's sphere of influence as +infinity.
        public static CelestialBody FromGame(string name, double radius, double mu,
                                             bool hasAtmosphere, double atmosphereDepth,
                                             double rotationPeriod, double sphereOfInfluence,
                                             double? semiMajorAxis, string? parentName)
        {
            bool isStar = parentName == null;
            double? soi = !isStar && IsPositiveFinite(sphereOfInfluence) ? sphereOfInfluence : (double?)null;

            return new CelestialBody(
                name, radius, mu,
                atm:           hasAtmosphere ? atmosphereDepth : 0.0,
                rotPeriod:     rotationPeriod,
                soi:           soi,
                orbitalRadius: isStar ? null : semiMajorAxis,
                parent:        parentName);
        }

        private static bool IsPositiveFinite(double v) =>
            !double.IsNaN(v) && !double.IsInfinity(v) && v > 0.0;
    }
}
