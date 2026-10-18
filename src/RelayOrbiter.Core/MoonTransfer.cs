using System;

namespace RelayOrbiter.Core
{
    // A Hohmann transfer from a circular orbit around a planet to one of its moons,
    // in the patched-conic approximation (circular, coplanar orbits). The plugin
    // refines the burn with KSP's own prediction; this gives the window and the
    // budget.
    public sealed class MoonTransferPlan
    {
        public MoonTransferPlan(double phaseAngleDeg, double transferSma, double transferTime, double ejectionDv,
                                double arrivalVInf, double captureDv)
        {
            PhaseAngleDeg = phaseAngleDeg;
            TransferSma   = transferSma;
            TransferTime  = transferTime;
            EjectionDv    = ejectionDv;
            ArrivalVInf   = arrivalVInf;
            CaptureDv     = captureDv;
        }

        // How far ahead of the vessel the moon must be at the ejection burn (degrees, 0..360).
        public double PhaseAngleDeg { get; }
        public double TransferSma   { get; }
        public double TransferTime  { get; }  // s from the ejection to the moon's orbit
        public double EjectionDv    { get; }  // prograde (negative: an outer orbit falling inwards)
        public double ArrivalVInf   { get; }  // m/s relative to the moon on arrival
        public double CaptureDv     { get; }  // at the periapsis, to a circular orbit (negative = retrograde)
        public double TotalDv => Math.Abs(EjectionDv) + Math.Abs(CaptureDv);
    }

    // (DIP) Depends on IOrbitalMechanics, not the concrete class.
    public sealed class MoonTransferPlanner
    {
        private readonly IOrbitalMechanics _mechanics;

        public MoonTransferPlanner(IOrbitalMechanics mechanics)
        {
            _mechanics = mechanics ?? throw new ArgumentNullException(nameof(mechanics));
        }

        // r1: the vessel's circular orbit radius; moonOrbit: the moon's orbit radius;
        // periapsis: the radius to capture at around the moon.
        public MoonTransferPlan Plan(double muParent, double r1, double moonOrbit, double muMoon, double periapsis)
        {
            double aT   = (r1 + moonOrbit) / 2.0;
            double tT   = _mechanics.OrbitalPeriod(aT, muParent) / 2.0;
            double tM   = _mechanics.OrbitalPeriod(moonOrbit, muParent);
            // The vessel arrives 180° from the burn; the moon must get there at the same time.
            double phase = StationKeeping.Mod360(180.0 - 360.0 * tT / tM);
            double ejection = _mechanics.VisViva(r1, aT, muParent) - _mechanics.VisViva(r1, r1, muParent);
            double vInf = Math.Abs(_mechanics.VisViva(moonOrbit, aT, muParent) - _mechanics.VisViva(moonOrbit, moonOrbit, muParent));
            double vPeri = Math.Sqrt(vInf * vInf + 2.0 * muMoon / periapsis);
            double capture = Math.Sqrt(muMoon / periapsis) - vPeri;
            return new MoonTransferPlan(phase, aT, tT, ejection, vInf, capture);
        }

        // The moon's angle ahead of the vessel (degrees, 0..360), from their longitudes.
        public static double PhaseAngleDeg(double vesselLongitudeDeg, double moonLongitudeDeg) =>
            StationKeeping.Mod360(moonLongitudeDeg - vesselLongitudeDeg);

        // Seconds until the phase angle reaches the required one. An inner vessel
        // gains on the moon (the angle shrinks), an outer one falls behind (it grows).
        public static double WaitTime(double phaseNowDeg, double phaseRequiredDeg, double vesselPeriod, double moonPeriod)
        {
            double rate = 360.0 / vesselPeriod - 360.0 / moonPeriod;  // deg/s the vessel gains
            if (Math.Abs(rate) < 1e-12) return double.PositiveInfinity;
            return rate > 0.0
                ? StationKeeping.Mod360(phaseNowDeg - phaseRequiredDeg) / rate
                : StationKeeping.Mod360(phaseRequiredDeg - phaseNowDeg) / -rate;
        }

        // Time between two windows: one synodic period.
        public static double SynodicPeriod(double vesselPeriod, double moonPeriod) =>
            1.0 / Math.Abs(1.0 / vesselPeriod - 1.0 / moonPeriod);
    }
}
