using System;

namespace RelayOrbiter.Core
{
    // (DIP) Depends on IOrbitalMechanics, not the concrete class.
    public sealed class DeploymentPlanner : IDeploymentPlanner
    {
        public const int Satellites = 3;

        private readonly IOrbitalMechanics _mechanics;

        public DeploymentPlanner(IOrbitalMechanics mechanics)
        {
            _mechanics = mechanics ?? throw new ArgumentNullException(nameof(mechanics));
        }

        public DeploymentPlan Plan(double mu, double apoapsisRadius, double semiMajorAxis, double apoapsisUT)
        {
            double r = apoapsisRadius;

            // Parking orbit with apoapsis r and T_e = (2/3) T_c(r)  =>  a_e = r (2/3)^(2/3)
            double aE     = r * Math.Pow(2.0 / 3.0, 2.0 / 3.0);
            double vNow   = _mechanics.VisViva(r, semiMajorAxis, mu);
            double vPark  = _mechanics.VisViva(r, aE, mu);
            double vCirc  = _mechanics.VisViva(r, r, mu);
            double tCirc  = _mechanics.OrbitalPeriod(r, mu);
            double tPark  = tCirc * (2.0 / 3.0);

            var releases = new double[Satellites];
            for (int k = 0; k < Satellites; ++k)
                releases[k] = apoapsisUT + k * tPark;

            return new DeploymentPlan(
                burnUT:                     apoapsisUT,
                releaseRadius:              r,
                parkingDeltaV:              vPark - vNow,
                periapsisRadius:            2.0 * aE - r,
                circularPeriod:             tCirc,
                parkingPeriod:              tPark,
                satelliteCircularizeDeltaV: vCirc - vPark,
                releaseUTs:                 releases);
        }

        public double CircularizeAtApoapsis(double mu, double apoapsisRadius, double semiMajorAxis) =>
            _mechanics.VisViva(apoapsisRadius, apoapsisRadius, mu)
            - _mechanics.VisViva(apoapsisRadius, semiMajorAxis, mu);

        public double NextApoapsisUT(double now, double timeToApoapsis, double period, double minLead) =>
            now + timeToApoapsis + (timeToApoapsis < minLead ? period : 0.0);
    }
}
