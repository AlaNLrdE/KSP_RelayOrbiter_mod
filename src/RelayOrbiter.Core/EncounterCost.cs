using System;

namespace RelayOrbiter.Core
{
    // How well a predicted arrival at a moon or planet meets the plan, lower is better, in
    // ordered bands:
    //   below 1   an encounter: the periapsis error, plus the inclination error when the
    //             plan has a target inclination;
    //   2..3      an impact (periapsis below the safe minimum);
    //   +5        arriving the wrong way round (retrograde for a prograde goal, or back);
    //   10 and up no encounter (EncounterSearch adds the closest approach).
    // Far out the arrival plane costs a few m/s to aim (a Duna correction of 3.8 m/s
    // arrived at 24.9°, one of 6.2 m/s at 0.4°); once captured, the same change cost 209 m/s.
    public static class EncounterCost
    {
        public const double RetrogradePenalty = 5.0;
        public const double DegreesPerError   = 100.0;  // 1° of inclination weighs as 1% of periapsis
        public const double PolarBandDeg      = 1.0;    // a polar goal: either way round

        // periapsis, targetPeriapsis, minPeriapsis: radii (m); inclinations around the
        // destination (°); goalInclinationDeg: null when the plan keeps the arrival plane.
        public static double Of(double periapsis, double inclinationDeg, double targetPeriapsis, double minPeriapsis,
                                double? goalInclinationDeg = null)
        {
            double cost;
            if (periapsis < minPeriapsis)
                cost = 2.0 + Math.Min(1.0, (minPeriapsis - periapsis) / minPeriapsis);
            else
            {
                // e/(1+e): below 1 however far off, and never flat, so a search always sees
                // which way is better. (Capped at 1, every periapsis more than 100% off looked
                // alike: a Duna arrival at 47,526 km, aiming at 1,320 km, found no correction.)
                double e = PeriapsisError(periapsis, targetPeriapsis);
                if (goalInclinationDeg.HasValue)
                    e += Math.Abs(inclinationDeg - goalInclinationDeg.Value) / DegreesPerError;
                cost = e / (1.0 + e);
            }
            if (WrongWay(inclinationDeg, goalInclinationDeg ?? 0.0)) cost += RetrogradePenalty;
            return cost;
        }

        public static double PeriapsisError(double periapsis, double targetPeriapsis) =>
            Math.Abs(periapsis - targetPeriapsis) / targetPeriapsis;

        // Prograde arrivals for a prograde goal (or none), retrograde ones for a retrograde goal.
        public static bool WrongWay(double inclinationDeg, double goalInclinationDeg) =>
            Math.Abs(goalInclinationDeg - 90.0) > PolarBandDeg && (inclinationDeg > 90.0) != (goalInclinationDeg > 90.0);
    }
}
