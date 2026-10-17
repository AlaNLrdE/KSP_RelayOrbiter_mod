using System;

namespace RelayOrbiter.Core
{
    public enum Apsis { Periapsis, Apoapsis }

    // Hohmann transfer from the current orbit to a circular orbit of radius rTarget.
    public sealed class TransferPlan
    {
        public TransferPlan(bool needed, Apsis firstBurnAt, double firstBurnRadius, double firstDv,
                            double transferSma, double secondDv)
        {
            Needed          = needed;
            FirstBurnAt     = firstBurnAt;
            FirstBurnRadius = firstBurnRadius;
            FirstDv         = firstDv;
            TransferSma     = transferSma;
            SecondDv        = secondDv;
        }

        public bool   Needed          { get; }  // false when already circular at the target
        public Apsis  FirstBurnAt     { get; }  // where the first burn happens
        public double FirstBurnRadius { get; }
        public double FirstDv         { get; }  // prograde Δv (negative = retrograde)
        public double TransferSma     { get; }
        public double SecondDv        { get; }  // prograde Δv to circularize at the target
        public double TotalDv => Math.Abs(FirstDv) + Math.Abs(SecondDv);
    }

    // (DIP) Depends on IOrbitalMechanics, not the concrete class.
    public sealed class TransferPlanner
    {
        public const double ToleranceM   = 1_000.0;  // already "at the target" within this...
        public const double ToleranceRel = 0.0005;   // ...or this fraction of the target radius (~1.7 km at Kerbin sync)

        private readonly IOrbitalMechanics _mechanics;

        public TransferPlanner(IOrbitalMechanics mechanics)
        {
            _mechanics = mechanics ?? throw new ArgumentNullException(nameof(mechanics));
        }

        // Burn at periapsis when the target is at or above it (raise, or lower only the
        // apoapsis), otherwise at apoapsis (lower the periapsis); then circularize.
        public TransferPlan Plan(double mu, double apoapsisRadius, double periapsisRadius, double rTarget)
        {
            double tol = Math.Max(ToleranceM, ToleranceRel * rTarget);
            if (Math.Abs(apoapsisRadius - rTarget) <= tol && Math.Abs(periapsisRadius - rTarget) <= tol)
                return new TransferPlan(false, Apsis.Periapsis, periapsisRadius, 0.0, 0.0, 0.0);

            double sma = (apoapsisRadius + periapsisRadius) / 2.0;
            var at = rTarget >= periapsisRadius ? Apsis.Periapsis : Apsis.Apoapsis;
            double r1 = at == Apsis.Periapsis ? periapsisRadius : apoapsisRadius;
            double aT = (r1 + rTarget) / 2.0;
            double dv1 = _mechanics.VisViva(r1, aT, mu) - _mechanics.VisViva(r1, sma, mu);
            double dv2 = _mechanics.VisViva(rTarget, rTarget, mu) - _mechanics.VisViva(rTarget, aT, mu);
            return new TransferPlan(true, at, r1, dv1, aT, dv2);
        }
    }

    // Rotating the orbit plane at a node of the orbit with the equator.
    public static class PlaneChange
    {
        public const double ToleranceDeg = 0.1;

        // Δv to rotate a velocity of the given speed from currentInc to targetInc (degrees)
        // at the ascending or descending node: (prograde, normal) components. At the
        // ascending node a higher inclination tilts the velocity towards the orbit
        // normal; at the descending node, away from it.
        public static (double prograde, double normal) DeltaV(double speed, double currentIncDeg,
                                                             double targetIncDeg, bool ascending)
        {
            double di = (targetIncDeg - currentIncDeg) * Math.PI / 180.0;
            double normal = speed * Math.Sin(di) * (ascending ? 1.0 : -1.0);
            return (speed * (Math.Cos(di) - 1.0), normal);
        }

        // True anomaly (degrees, 0..360) of the ascending or descending node.
        public static double NodeTrueAnomalyDeg(double argumentOfPeriapsisDeg, bool ascending)
        {
            double an = Mod360(360.0 - argumentOfPeriapsisDeg);
            return ascending ? an : Mod360(an + 180.0);
        }

        public static bool Needed(double currentIncDeg, double targetIncDeg) =>
            Math.Abs(targetIncDeg - currentIncDeg) > ToleranceDeg;

        private static double Mod360(double deg) => ((deg % 360.0) + 360.0) % 360.0;
    }
}
