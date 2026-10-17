namespace RelayOrbiter.Core
{
    // (ISP) Pure orbital-mechanics computations — no database or UI knowledge.
    public interface IOrbitalMechanics
    {
        double OrbitalPeriod(double semiMajorAxis, double mu);

        // Vis-viva: speed at radius r on an orbit with semi-major axis a.
        double VisViva(double r, double semiMajorAxis, double mu);

        // Synchronous orbit altitude above the surface.
        double? SyncOrbitAltitude(CelestialBody body);

        // SOI radius - body radius, or null for bodies without an SOI limit (Kerbol).
        double? SoiMaxAltitude(CelestialBody body);
    }
}
