namespace RelayOrbiter.Core
{
    public sealed class OrbitalMechanics : IOrbitalMechanics
    {
        // STUB — all functions return zero / null until implementation step.
        public double OrbitalPeriod(double semiMajorAxis, double mu) => 0.0;

        public double VisViva(double r, double semiMajorAxis, double mu) => 0.0;

        public double? SyncOrbitAltitude(CelestialBody body) => null;

        public double? SoiMaxAltitude(CelestialBody body) => null;
    }
}
