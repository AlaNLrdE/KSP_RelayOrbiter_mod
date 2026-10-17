using System;

namespace RelayOrbiter.Core
{
    public enum SeparationMode { Normal, Prograde }

    // Attitude for releasing a satellite so the separation push is perpendicular
    // to the velocity: then it barely changes the satellite's period, which is
    // what keeps the 120° spacing from drifting.
    public static class SeparationAttitude
    {
        public const double ToleranceDeg   = 3.0;   // max deviation from perpendicular
        public const double TimeoutSeconds = 60.0;  // release anyway after this long

        // angleToAxisDeg: angle between the separation axis and the vessel's
        // forward (control) axis.
        // Mostly along the axis → point normal (the push goes sideways). Mostly across
        // the axis → point prograde (the push is then perpendicular to the velocity
        // whatever the roll). Normal at exactly 45°.
        public static SeparationMode Choose(double angleToAxisDeg)
        {
            double a = Math.Abs(angleToAxisDeg) % 180.0;
            if (a > 90.0) a = 180.0 - a;
            return a <= 45.0 ? SeparationMode.Normal : SeparationMode.Prograde;
        }

        // angleToVelocityDeg: angle between the separation axis and the velocity.
        public static bool IsReady(double angleToVelocityDeg) =>
            Math.Abs(angleToVelocityDeg - 90.0) <= ToleranceDeg;
    }
}
