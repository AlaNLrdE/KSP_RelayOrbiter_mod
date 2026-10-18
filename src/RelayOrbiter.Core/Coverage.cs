using System;
using System.Collections.Generic;

namespace RelayOrbiter.Core
{
    // Whether neighbouring satellites of an evenly spaced constellation can talk:
    // line of sight past the body, and relay antenna range (KSP CommNet rules).
    public static class Coverage
    {
        // Closest approach of the line between two neighbours to the body's centre.
        public static double ChordClearance(double orbitRadius, int satellites) =>
            orbitRadius * Math.Cos(Math.PI / satellites);

        public static bool LineOfSight(double orbitRadius, double bodyRadius, int satellites) =>
            ChordClearance(orbitRadius, satellites) > bodyRadius;

        // Smallest orbit radius at which neighbours see each other (infinite for two
        // satellites, which are always on opposite sides of the body).
        public static double MinRadiusForLineOfSight(double bodyRadius, int satellites)
        {
            double c = Math.Cos(Math.PI / satellites);
            return c > 1e-9 ? bodyRadius / c : double.PositiveInfinity;
        }

        public static double NeighbourDistance(double orbitRadius, int satellites) =>
            2.0 * orbitRadius * Math.Sin(Math.PI / satellites);

        // KSP's combination of several antennas: the strongest power times
        // (sum / strongest) ^ (power-weighted average combinability exponent);
        // antennas that don't combine count only if they are the strongest.
        public static double CombinedPower(IReadOnlyList<(double power, bool combinable, double exponent)> antennas)
        {
            double strongest = 0.0, sum = 0.0, weighted = 0.0, strongestCombinable = 0.0;
            bool strongestCombines = false;
            foreach (var (power, combinable, exponent) in antennas)
            {
                if (power > strongest) { strongest = power; strongestCombines = combinable; }
                if (!combinable) continue;
                sum += power;
                weighted += power * exponent;
                strongestCombinable = Math.Max(strongestCombinable, power);
            }
            if (strongest <= 0.0) return 0.0;
            if (!strongestCombines) return strongest;
            return strongestCombinable * Math.Pow(sum / strongestCombinable, weighted / sum);
        }

        // CommNet range between two vessels: sqrt(P1 · P2).
        public static double Range(double powerA, double powerB) => Math.Sqrt(powerA * powerB);
    }
}
