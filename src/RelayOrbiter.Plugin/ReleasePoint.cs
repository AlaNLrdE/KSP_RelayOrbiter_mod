using RelayOrbiter.Core;

namespace RelayOrbiter.Plugin
{
    // Where on an orbit the satellites are released: the apoapsis for an inner
    // parking orbit, the periapsis for an outer one.
    internal static class ReleasePoint
    {
        public static double Radius(Orbit o, ParkingOrbit parking) =>
            parking == ParkingOrbit.Inner ? o.ApR : o.PeR;

        public static double TimeTo(Orbit o, ParkingOrbit parking) =>
            parking == ParkingOrbit.Inner ? o.timeToAp : o.timeToPe;

        public static string Name(ParkingOrbit parking) =>
            parking == ParkingOrbit.Inner ? "apoapsis" : "periapsis";

        // "prograde" / "retrograde" for a prograde Δv.
        public static string Direction(double progradeDv) => progradeDv >= 0.0 ? "prograde" : "retrograde";
    }
}
