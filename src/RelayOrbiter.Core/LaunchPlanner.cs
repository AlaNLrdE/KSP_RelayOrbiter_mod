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

        // The parking altitude, or the target itself when it is lower.
        public static double LaunchAltitude(CelestialBody body, double targetAltitude) =>
            Math.Min(targetAltitude, ParkingAltitude(body));
    }
}
