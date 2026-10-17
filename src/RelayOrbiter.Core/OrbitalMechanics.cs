using System;

namespace RelayOrbiter.Core
{
    public sealed class OrbitalMechanics : IOrbitalMechanics
    {
        // Kepler's third law: T = 2π √(a³ / μ)
        public double OrbitalPeriod(double semiMajorAxis, double mu) =>
            2.0 * Math.PI * Math.Sqrt(semiMajorAxis * semiMajorAxis * semiMajorAxis / mu);

        // v = √(μ (2/r − 1/a))
        public double VisViva(double r, double semiMajorAxis, double mu) =>
            Math.Sqrt(mu * (2.0 / r - 1.0 / semiMajorAxis));

        // r_sync = ∛(μ (T_rot / 2π)²); Math.Cbrt is not in netstandard2.0.
        public double? SyncOrbitAltitude(CelestialBody body)
        {
            double t = body.RotPeriod / (2.0 * Math.PI);
            double rSync = Math.Pow(body.Mu * t * t, 1.0 / 3.0);
            return rSync - body.Radius;
        }

        public double? SoiMaxAltitude(CelestialBody body) =>
            body.Soi.HasValue ? body.Soi.Value - body.Radius : (double?)null;
    }
}
