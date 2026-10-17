using System.Collections.Generic;

namespace RelayOrbiter.Core
{
    // Burns and timings to deploy 3 satellites from the carrier's current orbit.
    // Distances in metres, times in seconds (UT), velocities in m/s.
    public sealed class DeploymentPlan
    {
        public DeploymentPlan(double burnUT, double releaseRadius, double parkingDeltaV,
                              double periapsisRadius, double circularPeriod, double parkingPeriod,
                              double satelliteCircularizeDeltaV, IReadOnlyList<double> releaseUTs)
        {
            BurnUT                     = burnUT;
            ReleaseRadius              = releaseRadius;
            ParkingDeltaV              = parkingDeltaV;
            PeriapsisRadius            = periapsisRadius;
            CircularPeriod             = circularPeriod;
            ParkingPeriod              = parkingPeriod;
            SatelliteCircularizeDeltaV = satelliteCircularizeDeltaV;
            ReleaseUTs                 = releaseUTs;
        }

        public double BurnUT                     { get; }  // carrier burn, at apoapsis
        public double ReleaseRadius              { get; }  // apoapsis radius = constellation radius
        public double ParkingDeltaV              { get; }  // prograde Δv of the carrier burn (negative = retrograde)
        public double PeriapsisRadius            { get; }  // parking orbit periapsis radius
        public double CircularPeriod             { get; }  // satellites' final orbit period
        public double ParkingPeriod              { get; }  // carrier parking orbit period = (2/3) CircularPeriod
        public double SatelliteCircularizeDeltaV { get; }  // prograde Δv each satellite needs at apoapsis
        public IReadOnlyList<double> ReleaseUTs  { get; }  // SAT-1..SAT-3 release times
    }
}
