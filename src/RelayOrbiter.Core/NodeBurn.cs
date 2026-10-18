using System;

namespace RelayOrbiter.Core
{
    // Throttle and cut-off logic for executing one maneuver node.
    public sealed class NodeBurn
    {
        public const double MaxAlignmentDeg = 5.0;   // no thrust while pointing further off
        public const double DoneDv          = 0.05;  // m/s left: burn complete
        public const double PreciseDv       = 0.005; // m/s left: burn complete, precise burns
        public const double OvershootDv     = 0.5;   // m/s above the minimum seen: stop
        public const double MinThrottle     = 0.01;
        public const double TailSeconds     = 1.0;   // ease off during the last second of full thrust
        public const double TailHoldSeconds = 1.5;   // hold attitude for the last 1.5 s of full thrust

        // Last part of the burn: the remaining Δv vector starts swinging with every
        // small pointing error, so stop chasing it and hold attitude instead.
        public static bool IsTail(double dvLeft, double maxAccel) =>
            maxAccel <= 0.0 || dvLeft < maxAccel * TailHoldSeconds;

        private double _minDvLeft = double.PositiveInfinity;
        private readonly double _doneDv;

        // doneDv: PreciseDv for station keeping, where a few cm/s already make the
        // period drift noticeably on slow, high orbits.
        public NodeBurn(double doneDv = DoneDv)
        {
            _doneDv = doneDv;
        }

        // Throttle 0..1 for the remaining Δv (m/s), max acceleration (m/s²)
        // and pointing error (degrees).
        public static double Throttle(double dvLeft, double maxAccel, double alignmentDeg)
        {
            if (dvLeft <= 0.0 || maxAccel <= 0.0 || alignmentDeg > MaxAlignmentDeg) return 0.0;
            double t = Math.Max(MinThrottle, Math.Min(1.0, dvLeft / (maxAccel * TailSeconds)));
            double off = alignmentDeg / MaxAlignmentDeg;
            return t * (1.0 - off * off);
        }

        // Feed the remaining Δv every tick; true once the burn should stop.
        public bool Update(double dvLeft)
        {
            if (dvLeft < _doneDv) return true;
            _minDvLeft = Math.Min(_minDvLeft, dvLeft);
            return dvLeft > _minDvLeft + OvershootDv;
        }
    }
}
