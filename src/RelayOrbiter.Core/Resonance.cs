using System;

namespace RelayOrbiter.Core
{
    // Where the carrier's parking orbit sits relative to the satellites' final orbit.
    public enum ParkingOrbit
    {
        Inner,  // apoapsis on the target orbit, period (N−1)/N · T_c, released at apoapsis
        Outer,  // periapsis on the target orbit, period (N+1)/N · T_c, released at periapsis
    }

    // The resonance that spaces N satellites 360°/N apart: each parking orbit the
    // carrier drifts 1/N of a circular orbit relative to the satellites.
    public static class Resonance
    {
        public const int MinSatellites = 2;
        public const int MaxSatellites = 12;

        // T_parking / T_circular.
        public static double PeriodRatio(int satellites, ParkingOrbit parking)
        {
            Check(satellites);
            return parking == ParkingOrbit.Inner ? (satellites - 1.0) / satellites
                                                 : (satellites + 1.0) / satellites;
        }

        public static double SpacingDeg(int satellites)
        {
            Check(satellites);
            return 360.0 / satellites;
        }

        private static void Check(int satellites)
        {
            if (satellites < MinSatellites || satellites > MaxSatellites)
                throw new ArgumentOutOfRangeException(nameof(satellites), satellites,
                    $"Between {MinSatellites} and {MaxSatellites} satellites.");
        }
    }
}
