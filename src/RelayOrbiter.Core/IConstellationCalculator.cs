namespace RelayOrbiter.Core
{
    // (ISP) Computes the 3-satellite deployment orbits for a target altitude.
    public interface IConstellationCalculator
    {
        OrbitalResult Calculate(CelestialBody body, double hTarget);
    }
}
