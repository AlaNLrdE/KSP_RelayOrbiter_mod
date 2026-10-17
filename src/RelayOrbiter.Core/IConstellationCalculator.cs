namespace RelayOrbiter.Core
{
    // (ISP) Computes the deployment orbits of N satellites for a target altitude.
    public interface IConstellationCalculator
    {
        OrbitalResult Calculate(CelestialBody body, double hTarget,
                                int satellites = 3, ParkingOrbit parking = ParkingOrbit.Inner);

        // Inner when its periapsis is safe; otherwise outer, if it fits in the SOI.
        ParkingOrbit Recommend(CelestialBody body, double hTarget, int satellites);
    }
}
