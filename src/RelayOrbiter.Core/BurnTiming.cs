using System;

namespace RelayOrbiter.Core
{
    public static class BurnTiming
    {
        public const double G0 = 9.80665;  // m/s², standard gravity (KSP uses the same)

        // Burn duration in seconds for dv (m/s) with mass in t, thrust in kN, Isp in s
        // (rocket equation). Null when there is no thrust.
        public static double? Duration(double dv, double massTonnes, double thrustKN, double isp)
        {
            if (thrustKN <= 0.0 || isp <= 0.0) return null;
            double ve = isp * G0;
            // t·(m/s)/kN = s
            return massTonnes * ve / thrustKN * (1.0 - Math.Exp(-Math.Abs(dv) / ve));
        }
    }
}
