using System;
using System.Collections.Generic;
using System.Linq;

namespace RelayOrbiter.Core
{
    // Where each satellite should be: N slots 360°/N apart, starting at OffsetDeg.
    public sealed class SlotFit
    {
        public SlotFit(int slots, double offsetDeg, IReadOnlyList<int> slotOf, IReadOnlyList<double> errorsDeg)
        {
            Slots     = slots;
            OffsetDeg = offsetDeg;
            SlotOf    = slotOf;
            ErrorsDeg = errorsDeg;
        }

        public int    Slots     { get; }
        public double OffsetDeg { get; }  // mean longitude of slot 0
        public double SpacingDeg => 360.0 / Slots;
        public IReadOnlyList<int>    SlotOf    { get; }  // slot of each satellite (0..N−1)
        public IReadOnlyList<double> ErrorsDeg { get; }  // −180..180; positive = ahead of its slot

        public double SlotLongitudeDeg(int slot) => StationKeeping.Mod360(OffsetDeg + slot * SpacingDeg);
    }

    // Keeping a constellation evenly spaced. On rails KSP orbits are not perturbed:
    // spacing drifts only because periods differ, so a satellite is corrected by
    // matching the target period and, when it is off its slot, by a phasing orbit.
    public static class StationKeeping
    {
        public const double PhaseToleranceDeg  = 0.5;    // off its slot by less: fine
        public const double PeriodToleranceRel = 1e-4;   // period error (≈0.04°/orbit of drift)
        public const int    MaxPhasingOrbits   = 10;
        // A constellation kept in shape can be bigger than one carrier deploys (Resonance).
        public const int    MinSlots           = 2;
        public const int    MaxSlots           = 36;
        public const double ShapeToleranceRel  = 0.01;   // apsides this close to the target: no transfer

        // Fit N slots to the satellites' mean longitudes (degrees), keeping their order
        // around the orbit. reference: that satellite defines the slots (it needs no
        // correction). Otherwise the slots are the best fit, and `exclude` (the
        // satellite about to be corrected) does not pull them.
        public static SlotFit Fit(IReadOnlyList<double> longitudesDeg, int slots, int? reference = null,
                                  int? exclude = null)
        {
            int m = longitudesDeg.Count;
            if (m == 0) throw new ArgumentException("No satellites.", nameof(longitudesDeg));
            if (m > slots) throw new ArgumentException($"{m} satellites for {slots} slots.", nameof(slots));

            SlotFit? best = null;
            double bestCost = double.MaxValue;
            var origins = reference.HasValue ? new[] { reference.Value } : Enumerable.Range(0, m);
            foreach (int r in origins)
            {
                var slotOf = Assign(longitudesDeg, slots, r);
                double offset;
                if (reference.HasValue) offset = longitudesDeg[r];
                else
                {
                    var fitted = Enumerable.Range(0, m).Where(i => m == 1 || i != exclude).ToList();
                    offset = CircularMean(fitted.Select(i => longitudesDeg[i] - slotOf[i] * 360.0 / slots));
                }
                var errors = Enumerable.Range(0, m)
                    .Select(i => Wrap180(longitudesDeg[i] - offset - slotOf[i] * 360.0 / slots)).ToArray();
                double cost = Enumerable.Range(0, m).Where(i => i != exclude).Sum(i => errors[i] * errors[i]);
                if (cost < bestCost - 1e-9)
                {
                    bestCost = cost;
                    best = new SlotFit(slots, Mod360(offset), slotOf, errors);
                }
            }
            return best!;
        }

        // Slots in orbit order from satellite r (slot 0): each satellite takes the
        // slot nearest to it that is past the previous one and leaves room for the rest.
        private static int[] Assign(IReadOnlyList<double> lon, int slots, int r)
        {
            int m = lon.Count;
            double spacing = 360.0 / slots;
            var order = Enumerable.Range(0, m).OrderBy(i => i == r ? -1.0 : Mod360(lon[i] - lon[r])).ToList();
            var slotOf = new int[m];
            int previous = 0;
            for (int j = 1; j < m; ++j)
            {
                int i = order[j];
                int nearest = (int)Math.Round(Mod360(lon[i] - lon[r]) / spacing);
                int s = Math.Max(previous + 1, Math.Min(nearest, slots - (m - j)));
                slotOf[i] = s;
                previous = s;
            }
            return slotOf;
        }

        // The orbit every satellite should share: the synchronous one when the
        // constellation is synchronous, else the reference satellite's, else the mean
        // of the others (the satellite being corrected, `exclude`, is off by definition).
        public static double TargetSma(IReadOnlyList<double> smas, int? reference, double? syncSma,
                                       int? exclude = null)
        {
            if (syncSma.HasValue) return syncSma.Value;
            if (reference.HasValue) return smas[reference.Value];
            var others = smas.Where((_, i) => i != exclude).ToList();
            return (others.Count > 0 ? others : smas.ToList()).Average();
        }

        // Far enough from the target orbit to transfer (Hohmann) before phasing; closer
        // than that, the phasing burns and the period trim fix the period directly.
        public static bool NeedsTransfer(double apoapsisRadius, double periapsisRadius, double targetSma) =>
            Math.Abs(apoapsisRadius - targetSma) > ShapeToleranceRel * targetSma ||
            Math.Abs(periapsisRadius - targetSma) > ShapeToleranceRel * targetSma;

        public static double DriftDegPerOrbit(double period, double targetPeriod) =>
            -360.0 * (period - targetPeriod) / targetPeriod;

        public static bool PeriodOk(double period, double targetPeriod) =>
            Math.Abs(period - targetPeriod) <= PeriodToleranceRel * targetPeriod;

        public static bool PhaseOk(double errorDeg) => Math.Abs(errorDeg) <= PhaseToleranceDeg;

        public static double Mod360(double deg) => ((deg % 360.0) + 360.0) % 360.0;

        public static double Wrap180(double deg)
        {
            double d = Mod360(deg);
            return d > 180.0 ? d - 360.0 : d;
        }

        private static double CircularMean(IEnumerable<double> degrees)
        {
            double x = 0.0, y = 0.0;
            foreach (double d in degrees)
            {
                x += Math.Cos(d * Math.PI / 180.0);
                y += Math.Sin(d * Math.PI / 180.0);
            }
            return Mod360(Math.Atan2(y, x) * 180.0 / Math.PI);
        }
    }

    // Two burns at the same point, `Orbits` orbits apart: the first puts the
    // satellite on an orbit whose period makes it drift back to its slot, the second
    // sets the target period.
    public sealed class PhasingPlan
    {
        public PhasingPlan(int orbits, double errorDeg, double sma, double period, double otherApsisRadius,
                           double firstDv, double secondDv)
        {
            Orbits           = orbits;
            ErrorDeg         = errorDeg;
            Sma              = sma;
            Period           = period;
            OtherApsisRadius = otherApsisRadius;
            FirstDv          = firstDv;
            SecondDv         = secondDv;
        }

        public int    Orbits           { get; }
        public double ErrorDeg         { get; }  // phase removed (may be the long way round)
        public double Sma              { get; }  // phasing orbit
        public double Period           { get; }
        public double OtherApsisRadius { get; }  // the phasing orbit's apsis opposite the burn
        public double FirstDv          { get; }  // prograde (negative = retrograde)
        public double SecondDv         { get; }
        public double TotalDv  => Math.Abs(FirstDv) + Math.Abs(SecondDv);
        public double Duration => Orbits * Period;
    }

    // (DIP) Depends on IOrbitalMechanics, not the concrete class.
    public sealed class PhasingPlanner
    {
        private readonly IOrbitalMechanics _mechanics;

        public PhasingPlanner(IOrbitalMechanics mechanics)
        {
            _mechanics = mechanics ?? throw new ArgumentNullException(nameof(mechanics));
        }

        // errorDeg > 0: the satellite is ahead and must fall back, so its phasing orbit
        // is slower (longer). After k phasing orbits its slot has moved 360k·T_ph/T_t,
        // that is errorDeg more than the satellite: T_ph = T_t·(1 + error/(360·k)).
        public PhasingPlan Plan(double mu, double r, double currentSma, double targetSma, double errorDeg, int orbits)
        {
            if (orbits < 1) throw new ArgumentOutOfRangeException(nameof(orbits));
            double tTarget = _mechanics.OrbitalPeriod(targetSma, mu);
            double tPhase  = tTarget * (1.0 + errorDeg / (360.0 * orbits));
            double n       = tPhase / (2.0 * Math.PI);
            double sma     = Math.Pow(mu * n * n, 1.0 / 3.0);
            double vPhase  = _mechanics.VisViva(r, sma, mu);
            return new PhasingPlan(orbits, errorDeg, sma, tPhase, 2.0 * sma - r,
                                   vPhase - _mechanics.VisViva(r, currentSma, mu),
                                   _mechanics.VisViva(r, targetSma, mu) - vPhase);
        }

        // The quickest safe plan with at least minOrbits phasing orbits: the other apsis
        // must stay between minRadius and maxRadius. When falling back (or catching up)
        // is unsafe, try going the long way round. null when nothing fits.
        public PhasingPlan? Choose(double mu, double r, double currentSma, double targetSma, double errorDeg,
                                   double minRadius, double maxRadius, int minOrbits = 1,
                                   int maxOrbits = StationKeeping.MaxPhasingOrbits)
        {
            double longWay = errorDeg - 360.0 * Math.Sign(errorDeg);
            foreach (double error in new[] { errorDeg, longWay })
                for (int k = Math.Max(1, minOrbits); k <= maxOrbits; ++k)
                {
                    var p = Plan(mu, r, currentSma, targetSma, error, k);
                    if (p.OtherApsisRadius >= minRadius && p.OtherApsisRadius <= maxRadius &&
                        Math.Min(r, p.OtherApsisRadius) >= minRadius)
                        return p;
                }
            return null;
        }

        // One burn at radius r that sets the target period (the shape barely changes
        // when the orbit is already close to the target).
        public double TrimDv(double mu, double r, double currentSma, double targetSma) =>
            _mechanics.VisViva(r, targetSma, mu) - _mechanics.VisViva(r, currentSma, mu);

        // Lowest safe radius for a phasing orbit's periapsis.
        public static double MinRadius(CelestialBody body) =>
            body.Radius + LaunchPlanner.ParkingAltitude(body);
    }
}
