using System;

namespace RelayOrbiter.Core
{
    public enum AscentPhase { Vertical, GravityTurn, Coast, Circularize }

    // What the ascent autopilot should do now.
    public sealed class AscentCommand
    {
        public AscentCommand(AscentPhase phase, double pitchDeg, double throttle)
        {
            Phase    = phase;
            PitchDeg = pitchDeg;
            Throttle = throttle;
        }

        public AscentPhase Phase    { get; }
        public double      PitchDeg { get; }  // above the horizon
        public double      Throttle { get; }  // 0..1
    }

    // A classic gravity-turn ascent to a circular orbit:
    // 1. straight up until clear of the pad and of the thick low air;
    // 2. a pitch program by altitude, from vertical to horizontal at TurnEnd, full
    //    throttle until the apoapsis reaches the target;
    // 3. a coast (topping the apoapsis up while drag lowers it) until out of the
    //    atmosphere;
    // 4. the circularization, planned as soon as the vessel is out of the atmosphere
    //    (so it can point and time-warp to the burn), flown by the node executor.
    public sealed class AscentGuidance
    {
        public const double MinTurnStartAltitude = 1_000.0;  // m: straight up until at least here...
        public const double TurnStartFraction    = 0.05;     // ...or this much of the atmosphere (Kerbin: 3.5 km)
        public const double TurnStartSpeed       = 60.0;     // m/s: vertical phase over once this fast
        public const double TurnShape            = 0.5;      // pitch = 90°·(1 − x^shape)
        public const double TopUpMargin          = 1_000.0;  // m: apoapsis this far below: burn again
        public const double MaxAoADeg            = 5.0;      // nose this close to the airflow while the air pushes
        public const double AoAFadeoutPressure   = 2.5;      // kPa: below this the allowed angle widens with 1/q
        public const double MaxPressure          = 30.0;     // kPa: throttle down above this
        public const double DepartureMarginDeg   = 15.0;     // this far past the allowed angle: control is lost
        public const double DepartureSeconds     = 2.0;      // ...for this long (not a staging transient)
        public const double MaxQAlphaKPaDeg      = 60.0;     // q × angle of attack: the aerodynamic torque's measure
        public const double MinAoADeg            = 1.0;      // never tighter: the turn must still progress
        public const double MaxSlewInAirDegPerS  = 2.0;      // the commanded attitude turns no faster while the air pushes
        public const double MaxSlewFreeDegPerS   = 20.0;     // ...and in near-vacuum

        // Largest angle allowed between the nose and the surface velocity. The aerodynamic
        // torque grows with q × angle of attack, so the angle shrinks as q grows; 5° up to
        // 12 kPa. (MechJeb's own attitude control held 5° at 52 kPa on a SRB-heavy craft that
        // KSP's SAS, steering ours, let tumble at 43 kPa: a margin for SAS's overshoot.) Below the
        // fade-out pressure it widens with 1/q so the nose turns smoothly, not at once, to
        // the pitch program (MechJeb's AoA limit fade-out).
        public static double AllowedAoADeg(double dynamicPressureKPa) =>
            dynamicPressureKPa >= AoAFadeoutPressure
                ? Math.Max(MinAoADeg, Math.Min(MaxAoADeg, MaxQAlphaKPaDeg / dynamicPressureKPa))
            : dynamicPressureKPa <= 0.0 ? 180.0
            : Math.Min(180.0, MaxAoADeg * AoAFadeoutPressure / dynamicPressureKPa);

        // How fast the commanded attitude may turn. KSP's SAS, handed a step in thick air,
        // built a turn rate it could not stop (7.7°/s, nose 8.7° past the command) and the
        // air flipped the rocket; a gravity turn needs well under 1°/s there.
        public static double MaxSlewDegPerS(double dynamicPressureKPa) =>
            dynamicPressureKPa >= AoAFadeoutPressure ? MaxSlewInAirDegPerS
            : dynamicPressureKPa <= 0.0 ? MaxSlewFreeDegPerS
            : Math.Min(MaxSlewFreeDegPerS, MaxSlewInAirDegPerS * AoAFadeoutPressure / dynamicPressureKPa);

        // The nose far off the airflow while the air still pushes hard: the vessel is
        // tumbling, and steering along that airflow would only drive it down.
        public static bool IsDeparting(double angleOfAttackDeg, double dynamicPressureKPa) =>
            dynamicPressureKPa >= AoAFadeoutPressure &&
            angleOfAttackDeg > AllowedAoADeg(dynamicPressureKPa) + DepartureMarginDeg;

        // Straight up through the thick low air: the turn then starts where the dynamic
        // pressure is lower, instead of holding the angle of attack at its limit through
        // max-q (MechJeb's automatic turn start). Airless: just clear of the pad.
        public static double TurnStartAltitude(double atmosphereHeight) =>
            Math.Max(MinTurnStartAltitude, TurnStartFraction * atmosphereHeight);

        // Throttle limited so the dynamic pressure stays under MaxPressure.
        public static double LimitThrottle(double throttle, double dynamicPressureKPa) =>
            dynamicPressureKPa <= MaxPressure ? throttle
            : Math.Max(0.1, throttle * MaxPressure / dynamicPressureKPa);

        private readonly double _target, _atmosphere, _turnStart, _turnEnd;

        // targetAltitude: the orbit's; atmosphereHeight: 0 when airless.
        public AscentGuidance(double targetAltitude, double atmosphereHeight)
        {
            _target = targetAltitude;
            _atmosphere = atmosphereHeight;
            _turnStart = TurnStartAltitude(atmosphereHeight);
            _turnEnd = TurnEndAltitude(targetAltitude, atmosphereHeight);
        }

        // Horizontal by 85% of the atmosphere (Kerbin: about 60 km); airless, early,
        // there being no drag to fight: a third of the way to the target.
        public static double TurnEndAltitude(double targetAltitude, double atmosphereHeight) =>
            atmosphereHeight > 0.0 ? 0.85 * atmosphereHeight
                                   : Math.Max(5_000.0, targetAltitude / 3.0);

        public static double PitchDeg(double altitude, double turnStart, double turnEnd)
        {
            if (altitude <= turnStart) return 90.0;
            double x = Math.Min(1.0, (altitude - turnStart) / (turnEnd - turnStart));
            return 90.0 * (1.0 - Math.Pow(x, TurnShape));
        }

        public AscentCommand Update(double altitude, double surfaceSpeed, double apoapsisAltitude, double timeToApoapsis)
        {
            if (altitude < _turnStart && surfaceSpeed < TurnStartSpeed)
                return new AscentCommand(AscentPhase.Vertical, 90.0, 1.0);

            double pitch = PitchDeg(altitude, _turnStart, _turnEnd);
            if (apoapsisAltitude < _target)
            {
                // Ease off near the target so the apoapsis doesn't overshoot.
                double left = _target - apoapsisAltitude;
                double throttle = Math.Max(0.05, Math.Min(1.0, left / 5_000.0));
                bool topUp = altitude > _turnEnd && apoapsisAltitude > _target - TopUpMargin;
                return new AscentCommand(AscentPhase.GravityTurn, pitch, topUp ? Math.Min(throttle, 0.2) : throttle);
            }
            // Out of the air the apoapsis no longer drops: the circularization can be planned.
            if (altitude > _atmosphere)
                return new AscentCommand(AscentPhase.Circularize, 0.0, 0.0);
            return new AscentCommand(AscentPhase.Coast, pitch, 0.0);
        }

        // Compass heading (degrees, 0 north, 90 east) to launch into inclination i from
        // latitude φ, northbound, corrected for the surface's own eastward speed. An
        // inclination below the latitude can't be reached: then due east (or west).
        public static double LaunchHeadingDeg(double inclinationDeg, double latitudeDeg, double orbitalSpeed,
                                              double surfaceSpeed)
        {
            double phi = latitudeDeg * Math.PI / 180.0;
            double ratio = Math.Cos(inclinationDeg * Math.PI / 180.0) / Math.Cos(phi);
            ratio = Math.Max(-1.0, Math.Min(1.0, ratio));
            double beta = Math.Asin(ratio);  // inertial heading from north
            double vEast = orbitalSpeed * Math.Sin(beta) - surfaceSpeed * Math.Cos(phi);
            double vNorth = orbitalSpeed * Math.Cos(beta);
            return StationKeeping.Mod360(Math.Atan2(vEast, vNorth) * 180.0 / Math.PI);
        }
    }
}
