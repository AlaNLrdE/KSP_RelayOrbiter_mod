using System;

namespace RelayOrbiter.Core
{
    // Altitude of the low orbit a launch aims for before transferring to the target.
    public static class LaunchPlanner
    {
        public const double AboveAtmosphere = 30_000.0;  // m
        public const double MinAirless      = 20_000.0;  // m: clears the terrain of small moons
        public const double AirlessFraction = 0.05;      // of the radius, for big airless bodies

        public static double ParkingAltitude(CelestialBody body) =>
            body.Atm > 0.0 ? body.Atm + AboveAtmosphere
                           : Math.Max(MinAirless, AirlessFraction * body.Radius);

        public const double AtmosphereLosses = 1300.0;  // m/s of gravity and drag losses with an atmosphere
        public const double AirlessLosses    = 300.0;   // m/s without one (gravity losses)

        // Rough Δv from the ground to a circular orbit at altitude and inclination: the
        // orbital speed plus losses, less the rotation's help (cos i of the surface speed:
        // a retrograde orbit fights it). About 3,400 m/s for low Kerbin orbit.
        public static double AscentDeltaV(CelestialBody body, double altitude, double inclinationDeg)
        {
            double r = body.Radius + altitude;
            double orbital = Math.Sqrt(body.Mu / r);
            double surface = body.RotPeriod > 0.0 ? 2.0 * Math.PI * body.Radius / body.RotPeriod : 0.0;
            double losses = body.Atm > 0.0 ? AtmosphereLosses : AirlessLosses;
            return orbital + losses - surface * Math.Cos(inclinationDeg * Math.PI / 180.0);
        }

        // The parking altitude, or the target itself when it is lower.
        public static double LaunchAltitude(CelestialBody body, double targetAltitude) =>
            Math.Min(targetAltitude, ParkingAltitude(body));
    }
}
