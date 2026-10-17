using System;

namespace RelayOrbiter.Core
{
    // (DIP) Depends on IOrbitalMechanics, not the concrete class.
    public sealed class DeploymentPlanner : IDeploymentPlanner
    {

        private readonly IOrbitalMechanics _mechanics;

        public DeploymentPlanner(IOrbitalMechanics mechanics)
        {
            _mechanics = mechanics ?? throw new ArgumentNullException(nameof(mechanics));
        }

        public DeploymentPlan Plan(double mu, double apoapsisRadius, double semiMajorAxis, double apoapsisUT,
                                   int satellites = 3, ParkingOrbit parking = ParkingOrbit.Inner)
        {
            double r = apoapsisRadius;  // release radius (the periapsis for an outer orbit)

            // Parking orbit through r with T_e = ratio · T_c(r)  =>  a_e = r · ratio^(2/3)
            double ratio  = Resonance.PeriodRatio(satellites, parking);
            double aE     = r * Math.Pow(ratio, 2.0 / 3.0);
            double vNow   = _mechanics.VisViva(r, semiMajorAxis, mu);
            double vPark  = _mechanics.VisViva(r, aE, mu);
            double vCirc  = _mechanics.VisViva(r, r, mu);
            double tCirc  = _mechanics.OrbitalPeriod(r, mu);
            double tPark  = tCirc * ratio;
            double other  = 2.0 * aE - r;

            var releases = new double[satellites];
            for (int k = 0; k < satellites; ++k)
                releases[k] = apoapsisUT + k * tPark;

            bool inner = parking == ParkingOrbit.Inner;
            return new DeploymentPlan(
                burnUT:                     apoapsisUT,
                releaseRadius:              r,
                parkingDeltaV:              vPark - vNow,
                periapsisRadius:            inner ? other : r,
                circularPeriod:             tCirc,
                parkingPeriod:              tPark,
                satelliteCircularizeDeltaV: vCirc - vPark,
                releaseUTs:                 releases)
            {
                Parking        = parking,
                ApoapsisRadius = inner ? r : other,
            };
        }

        public double CircularizeAt(double mu, double r, double semiMajorAxis) =>
            _mechanics.VisViva(r, r, mu) - _mechanics.VisViva(r, semiMajorAxis, mu);

        public double NextPassUT(double now, double timeToPoint, double period, double minLead) =>
            now + timeToPoint + (timeToPoint < minLead ? period : 0.0);
    }
}
