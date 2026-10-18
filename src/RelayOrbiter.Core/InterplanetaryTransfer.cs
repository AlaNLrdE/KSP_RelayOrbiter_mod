using System;

namespace RelayOrbiter.Core
{
    // A Hohmann transfer between two bodies orbiting the same parent (planets around
    // the Sun, or two moons of a planet), from a circular parking orbit around the
    // first to a capture around the second; patched conics, circular coplanar orbits.
    public sealed class InterplanetaryPlan
    {
        public InterplanetaryPlan(double phaseAngleDeg, double transferSma, double transferTime,
                                  double departureVInf, double ejectionDv, double arrivalVInf, double captureDv,
                                  bool outward)
        {
            PhaseAngleDeg = phaseAngleDeg;
            TransferSma   = transferSma;
            TransferTime  = transferTime;
            DepartureVInf = departureVInf;
            EjectionDv    = ejectionDv;
            ArrivalVInf   = arrivalVInf;
            CaptureDv     = captureDv;
            Outward       = outward;
        }

        // How far ahead of the departure body the destination must be at departure (0..360).
        public double PhaseAngleDeg { get; }
        public double TransferSma   { get; }
        public double TransferTime  { get; }  // s from departure to arrival
        public double DepartureVInf { get; }  // m/s leaving the departure body's SOI
        public double EjectionDv    { get; }  // m/s from the parking orbit (always prograde)
        public double ArrivalVInf   { get; }
        public double CaptureDv     { get; }  // negative: retrograde at the periapsis
        public bool   Outward       { get; }  // destination farther from the parent: leave along the planet's motion
        public double TotalDv => EjectionDv + Math.Abs(CaptureDv);
    }

    // (DIP) Depends on IOrbitalMechanics, not the concrete class.
    public sealed class InterplanetaryPlanner
    {
        private readonly IOrbitalMechanics _mechanics;

        public InterplanetaryPlanner(IOrbitalMechanics mechanics)
        {
            _mechanics = mechanics ?? throw new ArgumentNullException(nameof(mechanics));
        }

        // muParent: the shared parent; from/to: the two bodies' orbit radii around it;
        // parking: the circular orbit radius around the departure body; periapsis: the
        // capture radius around the destination.
        public InterplanetaryPlan Plan(double muParent, double from, double to, double muFrom, double parking,
                                       double muTo, double periapsis)
        {
            double aT = (from + to) / 2.0;
            double tT = _mechanics.OrbitalPeriod(aT, muParent) / 2.0;
            double tTo = _mechanics.OrbitalPeriod(to, muParent);
            double phase = StationKeeping.Mod360(180.0 - 360.0 * tT / tTo);

            double vInfOut = Math.Abs(_mechanics.VisViva(from, aT, muParent) - _mechanics.VisViva(from, from, muParent));
            double ejection = Math.Sqrt(vInfOut * vInfOut + 2.0 * muFrom / parking) - Math.Sqrt(muFrom / parking);

            double vInfIn = Math.Abs(_mechanics.VisViva(to, aT, muParent) - _mechanics.VisViva(to, to, muParent));
            double capture = Math.Sqrt(muTo / periapsis) - Math.Sqrt(vInfIn * vInfIn + 2.0 * muTo / periapsis);
            return new InterplanetaryPlan(phase, aT, tT, vInfOut, ejection, vInfIn, capture, to > from);
        }
    }

    // How to get from the body orbited to a destination, as one or two legs.
    public enum TransferRoute
    {
        None,          // already there
        Moon,          // the destination orbits the body orbited
        Sibling,       // both orbit the same parent (Kerbin → Duna, Mun → Minmus)
        SiblingsMoon,  // a moon of a sibling: sibling first, then its moon (Kerbin → Duna → Ike)
        Unsupported,
    }

    public static class Routes
    {
        // parentOf: a body's parent name (null for the root).
        public static TransferRoute Find(string from, string to, Func<string, string?> parentOf)
        {
            if (from == to) return TransferRoute.None;
            string? pFrom = parentOf(from), pTo = parentOf(to);
            if (pTo == from) return TransferRoute.Moon;
            if (pFrom != null && pTo == pFrom) return TransferRoute.Sibling;
            if (pFrom != null && pTo != null && parentOf(pTo) == pFrom) return TransferRoute.SiblingsMoon;
            return TransferRoute.Unsupported;
        }

        // The last leg goes to a moon of the body the carrier is around by then.
        public static bool EndsAtMoon(TransferRoute r) => r == TransferRoute.Moon || r == TransferRoute.SiblingsMoon;

        // The first leg goes to a sibling of the body orbited.
        public static bool Interplanetary(TransferRoute r) => r == TransferRoute.Sibling || r == TransferRoute.SiblingsMoon;
    }
}
