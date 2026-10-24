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
    // 1. straight up until clear of the pad;
    // 2. a pitch program by altitude, from vertical to horizontal at TurnEnd, full
    //    throttle until the apoapsis reaches the target;
    // 3. a coast (topping the apoapsis up while drag lowers it) until out of the
    //    atmosphere;
    // 4. the circularization, planned as soon as the vessel is out of the atmosphere
    //    (so it can point and time-warp to the burn), flown by the node executor.
    public sealed class AscentGuidance
    {
        public const double TurnStartAltitude = 1_000.0;  // m: straight up until here...
        public const double TurnStartSpeed    = 60.0;     // ...or this fast (m/s)
        public const double TurnShape         = 0.5;      // pitch = 90°·(1 − x^shape)
        public const double TopUpMargin       = 1_000.0;  // m: apoapsis this far below: burn again
        public const double MaxAoADeg         = 5.0;      // nose this close to the airflow while the air pushes
        public const double AoAFreePressure   = 1.0;      // kPa: below this the angle of attack is free
        public const double MaxPressure       = 30.0;     // kPa: throttle down above this

        // Largest angle allowed between the nose and the surface velocity: tight while the
        // air pushes (a big angle of attack tears weak joints off), free in near-vacuum.
        public static double AllowedAoADeg(double dynamicPressureKPa) =>
            dynamicPressureKPa < AoAFreePressure ? 180.0 : MaxAoADeg;

        // Throttle limited so the dynamic pressure stays under MaxPressure.
        public static double LimitThrottle(double throttle, double dynamicPressureKPa) =>
            dynamicPressureKPa <= MaxPressure ? throttle
            : Math.Max(0.1, throttle * MaxPressure / dynamicPressureKPa);

        private readonly double _target, _atmosphere, _turnEnd;

        // targetAltitude: the orbit's; atmosphereHeight: 0 when airless.
        public AscentGuidance(double targetAltitude, double atmosphereHeight)
        {
            _target = targetAltitude;
            _atmosphere = atmosphereHeight;
            _turnEnd = TurnEndAltitude(targetAltitude, atmosphereHeight);
        }

        // Horizontal by 85% of the atmosphere (Kerbin: about 60 km); airless, early,
        // there being no drag to fight: a third of the way to the target.
        public static double TurnEndAltitude(double targetAltitude, double atmosphereHeight) =>
            atmosphereHeight > 0.0 ? 0.85 * atmosphereHeight
                                   : Math.Max(5_000.0, targetAltitude / 3.0);

        public static double PitchDeg(double altitude, double turnEnd)
        {
            if (altitude <= TurnStartAltitude) return 90.0;
            double x = Math.Min(1.0, (altitude - TurnStartAltitude) / (turnEnd - TurnStartAltitude));
            return 90.0 * (1.0 - Math.Pow(x, TurnShape));
        }

        public AscentCommand Update(double altitude, double surfaceSpeed, double apoapsisAltitude, double timeToApoapsis)
        {
            if (altitude < TurnStartAltitude && surfaceSpeed < TurnStartSpeed)
                return new AscentCommand(AscentPhase.Vertical, 90.0, 1.0);

            double pitch = PitchDeg(altitude, _turnEnd);
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
