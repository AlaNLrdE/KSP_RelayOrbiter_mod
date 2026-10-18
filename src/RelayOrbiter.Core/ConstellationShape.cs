using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RelayOrbiter.Core
{
    public enum ShapeKind
    {
        Ring,         // one plane, N evenly spaced (what a carrier deploys)
        WalkerDelta,  // i:T/P/F, P planes with their nodes 360°/P apart
        WalkerStar,   // the same with the nodes 180°/P apart (near-polar planes)
        Molniya,      // elliptical, period half the rotation, apoapsis over one hemisphere
        Tundra,       // elliptical, period equal to the rotation
        Tetrahedral,  // Draim's four elliptical orbits: the whole surface always sees one
        Trojans,      // in a moon's orbit, 60° ahead and behind (and opposite it)
    }

    // One satellite's place in a constellation: its orbit and its phase. The node and
    // the phase are relative to the constellation's own reference (Ω0, θ0), fitted to
    // the satellites; Trojans are relative to their moon.
    public sealed class SlotOrbit
    {
        public SlotOrbit(int index, int plane, double inclinationDeg, double lanOffsetDeg, double sma,
                         double eccentricity, double argPeDeg, double phaseOffsetDeg)
        {
            Index          = index;
            Plane          = plane;
            InclinationDeg = inclinationDeg;
            LanOffsetDeg   = lanOffsetDeg;
            Sma            = sma;
            Eccentricity   = eccentricity;
            ArgPeDeg       = argPeDeg;
            PhaseOffsetDeg = phaseOffsetDeg;
        }

        public int    Index          { get; }
        public int    Plane          { get; }
        public double InclinationDeg { get; }
        public double LanOffsetDeg   { get; }  // node = Ω0 + this
        public double Sma            { get; }
        public double Eccentricity   { get; }
        public double ArgPeDeg       { get; }  // where the periapsis is (elliptical slots)
        public double PhaseOffsetDeg { get; }  // mean argument of latitude = θ0 + n·t + this
        public bool   Elliptical     => Eccentricity > StationKeeping.CircularEccentricity;
        public double PeriapsisRadius => Sma * (1.0 - Eccentricity);
        public double ApoapsisRadius  => Sma * (1.0 + Eccentricity);
    }

    // A constellation's shape and its parameters; Code() is what goes in the names.
    public sealed class ShapeSpec
    {
        public ShapeSpec(ShapeKind kind, int total, int planes, int phasing, double inclinationDeg, double sma,
                         double eccentricity = 0.0, double argPeDeg = 0.0)
        {
            if (total < 1) throw new ArgumentOutOfRangeException(nameof(total));
            if (planes < 1 || total % planes != 0)
                throw new ArgumentException($"{total} satellites don't split into {planes} planes.", nameof(planes));
            Kind           = kind;
            Total          = total;
            Planes         = planes;
            Phasing        = ((phasing % planes) + planes) % planes;
            InclinationDeg = inclinationDeg;
            Sma            = sma;
            Eccentricity   = eccentricity;
            ArgPeDeg       = argPeDeg;
        }

        public ShapeKind Kind  { get; }
        public int    Total    { get; }  // T
        public int    Planes   { get; }  // P
        public int    PerPlane => Total / Planes;
        public int    Phasing  { get; }  // F (0..P−1)
        public double InclinationDeg { get; }
        public double Sma      { get; }
        public double Eccentricity { get; }
        public double ArgPeDeg { get; }

        // One satellite per plane: a carrier can't deploy them together.
        public bool OnePerPlane => Planes == Total && Planes > 1;
        public bool MovesWithMoon => Kind == ShapeKind.Trojans;

        // The name tag ("WD55-3-1"); empty for a ring (names stay as they were).
        public string Code()
        {
            string i = Math.Round(InclinationDeg).ToString("F0", CultureInfo.InvariantCulture);
            return Kind switch
            {
                ShapeKind.WalkerDelta => $"WD{i}-{Planes}-{Phasing}",
                ShapeKind.WalkerStar  => $"WS{i}-{Planes}-{Phasing}",
                ShapeKind.Molniya     => $"MO{i}",
                ShapeKind.Tundra      => $"TU{i}",
                ShapeKind.Tetrahedral => "TH",
                ShapeKind.Trojans     => "TR",
                _                     => "",
            };
        }
    }

    public static class ConstellationShapes
    {
        public const double DraimEccentricity = 0.263;  // Draim's 4-satellite constellation
        public const double DraimInclination  = 31.3;
        public const double DraimPeriodFactor = 1.1;    // about 1.1 rotations

        public static ShapeSpec Ring(int n, double sma, double inclinationDeg = 0.0) =>
            new ShapeSpec(ShapeKind.Ring, n, 1, 0, inclinationDeg, sma);

        public static ShapeSpec WalkerDelta(int total, int planes, int phasing, double inclinationDeg, double sma) =>
            new ShapeSpec(ShapeKind.WalkerDelta, total, planes, phasing, inclinationDeg, sma);

        public static ShapeSpec WalkerStar(int total, int planes, int phasing, double inclinationDeg, double sma) =>
            new ShapeSpec(ShapeKind.WalkerStar, total, planes, phasing, inclinationDeg, sma);

        // Highly elliptical with the apoapsis over the north (ω = 270°): Molniya's period
        // is half the rotation, Tundra's the whole rotation. The periapsis is set by the
        // safe altitude; one satellite per plane, planes 360°/n apart.
        public static ShapeSpec Elliptical(ShapeKind kind, int count, double mu, double rotationPeriod,
                                           double periapsisRadius, double inclinationDeg)
        {
            if (kind != ShapeKind.Molniya && kind != ShapeKind.Tundra) throw new ArgumentException(nameof(kind));
            double period = kind == ShapeKind.Molniya ? rotationPeriod / 2.0 : rotationPeriod;
            double sma = SmaForPeriod(mu, period);
            double e = Math.Max(0.0, 1.0 - periapsisRadius / sma);
            return new ShapeSpec(kind, count, count, 0, inclinationDeg, sma, e, 270.0);
        }

        public static ShapeSpec Tetrahedral(double mu, double rotationPeriod) =>
            new ShapeSpec(ShapeKind.Tetrahedral, 4, 4, 0, DraimInclination,
                          SmaForPeriod(mu, DraimPeriodFactor * rotationPeriod), DraimEccentricity, 270.0);

        // In the moon's orbit (its plane, size and shape); count 2 (60° ahead and
        // behind) or 3 (and opposite the moon). Fit them with the moon's node and phase.
        public static ShapeSpec Trojans(int count, double moonSma, double moonEccentricity, double moonInclinationDeg,
                                        double moonArgPeDeg) =>
            new ShapeSpec(ShapeKind.Trojans, Math.Max(1, Math.Min(3, count)), 1, 0, moonInclinationDeg, moonSma,
                          moonEccentricity, moonArgPeDeg);

        public static double SmaForPeriod(double mu, double period)
        {
            double n = period / (2.0 * Math.PI);
            return Math.Pow(mu * n * n, 1.0 / 3.0);
        }

        public static IReadOnlyList<SlotOrbit> Slots(ShapeSpec s)
        {
            var slots = new List<SlotOrbit>();
            switch (s.Kind)
            {
                case ShapeKind.Trojans:
                {
                    // Plane and node are the moon's (offset 0); phases from the moon's.
                    var offsets = new[] { 60.0, -60.0, 180.0 };
                    for (int k = 0; k < s.Total; ++k)
                        slots.Add(new SlotOrbit(k, 0, s.InclinationDeg, 0.0, s.Sma, s.Eccentricity, s.ArgPeDeg,
                                                StationKeeping.Mod360(offsets[k % 3])));
                    break;
                }
                case ShapeKind.Tetrahedral:
                    // Nodes 90° apart, mean anomalies 90° apart the other way.
                    for (int k = 0; k < 4; ++k)
                        slots.Add(new SlotOrbit(k, k, s.InclinationDeg, 90.0 * k, s.Sma, s.Eccentricity, s.ArgPeDeg,
                                                StationKeeping.Mod360(-90.0 * k)));
                    break;
                default:
                {
                    // Walker i:T/P/F (a ring is P = 1): node p·ΔΩ, phase s·360/S + p·F·360/T.
                    double nodeSpread = s.Kind == ShapeKind.WalkerStar ? 180.0 : 360.0;
                    int perPlane = s.PerPlane;
                    for (int p = 0; p < s.Planes; ++p)
                        for (int k = 0; k < perPlane; ++k)
                            slots.Add(new SlotOrbit(p * perPlane + k, p, s.InclinationDeg, p * nodeSpread / s.Planes,
                                                    s.Sma, s.Eccentricity, s.ArgPeDeg,
                                                    StationKeeping.Mod360(k * 360.0 / perPlane +
                                                                          p * s.Phasing * 360.0 / s.Total)));
                    break;
                }
            }
            return slots;
        }

        // Back from a name tag (the part after "SYNC "/"1500K "); null when there is none.
        public static ShapeKind? KindFromTag(string tag)
        {
            foreach (var word in tag.Split(' '))
            {
                if (word.StartsWith("WD", StringComparison.Ordinal)) return ShapeKind.WalkerDelta;
                if (word.StartsWith("WS", StringComparison.Ordinal)) return ShapeKind.WalkerStar;
                if (word.StartsWith("MO", StringComparison.Ordinal)) return ShapeKind.Molniya;
                if (word.StartsWith("TU", StringComparison.Ordinal)) return ShapeKind.Tundra;
                if (word == "TH") return ShapeKind.Tetrahedral;
                if (word == "TR") return ShapeKind.Trojans;
            }
            return null;
        }

        // Walker parameters back from a tag word "WD55-3-1": (inclination, planes, phasing).
        public static (double inclination, int planes, int phasing)? WalkerFromTag(string tag)
        {
            foreach (var word in tag.Split(' '))
            {
                if (!(word.StartsWith("WD", StringComparison.Ordinal) || word.StartsWith("WS", StringComparison.Ordinal)))
                    continue;
                var parts = word.Substring(2).Split('-');
                if (parts.Length == 3 &&
                    double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double i) &&
                    int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) &&
                    int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int f))
                    return (i, p, f);
            }
            return null;
        }
    }
}
